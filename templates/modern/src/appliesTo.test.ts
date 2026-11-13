// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import test from 'node:test'
import assert from 'node:assert'
import { axisKey, canReference, chooseGroup, frameworkLabel, isAvailable, isValidFramework, parseUnits, rank, sortFrameworks, unitsOf } from './appliesTo'

test('parse units', () => {
  assert.deepStrictEqual(parseUnits('net8.0 netstandard2.0'), [{ framework: 'net8.0' }, { framework: 'netstandard2.0' }])
  assert.deepStrictEqual(parseUnits(' ef9:net8.0  ef3:net462 '), [{ value: 'ef9', framework: 'net8.0' }, { value: 'ef3', framework: 'net462' }])
  assert.deepStrictEqual(parseUnits(''), [])
  assert.deepStrictEqual(
    unitsOf([{ version: 'ef3', label: 'EF Core 3.1', frameworks: ['netstandard2.0', 'net462'] }, { frameworks: ['net8.0'] }]),
    [{ value: 'ef3', framework: 'netstandard2.0' }, { value: 'ef3', framework: 'net462' }, { framework: 'net8.0' }])
})

test('can reference', () => {
  assert.ok(canReference('net10.0', 'net8.0'))
  assert.ok(canReference('net10.0', 'netstandard2.1'))
  assert.ok(canReference('net8.0', 'netstandard2.0'))
  assert.ok(canReference('netcoreapp2.1', 'netstandard2.0'))
  assert.ok(canReference('net462', 'netstandard2.0'))
  assert.ok(canReference('net472', 'net462'))
  assert.ok(canReference('net8.0-windows', 'net8.0'))

  assert.ok(!canReference('net8.0', 'net10.0'))
  assert.ok(!canReference('net8.0', 'net462'))
  assert.ok(!canReference('net462', 'net8.0'))
  assert.ok(!canReference('net462', 'netstandard2.1'))
  assert.ok(!canReference('net46', 'netstandard2.0'))
  assert.ok(!canReference('netcoreapp2.1', 'netstandard2.1'))
  assert.ok(!canReference('net8.0', 'net8.0-windows'))
})

// The cases of TargetFrameworkHelperTest.OrderByNearest, so that the page picks the framework `docfx metadata` would.
test('nearest framework first', () => {
  const cases: [string, string, string][] = [
    ['net10.0', 'net462 netstandard2.0 net8.0 net10.0', 'net10.0 net8.0 netstandard2.0 net462'],
    ['net10.0', 'net462 netstandard2.0', 'netstandard2.0 net462'],
    ['net48', 'netstandard2.0 net8.0 net462', 'net462 netstandard2.0 net8.0'],
    ['net8.0', 'netstandard2.0 netstandard2.1 netcoreapp3.1', 'netcoreapp3.1 netstandard2.1 netstandard2.0'],
    ['net472', 'netstandard2.1 netstandard2.0 netstandard1.3', 'netstandard2.0 netstandard1.3 netstandard2.1'],
    ['net45', 'netstandard1.3 netstandard1.1', 'netstandard1.1 netstandard1.3'],
    ['netcoreapp2.1', 'netstandard2.1 netstandard2.0', 'netstandard2.0 netstandard2.1'],
    ['net8.0', 'net8.0-windows netstandard2.0', 'netstandard2.0 net8.0-windows'],
    ['net8.0-windows', 'net8.0 net8.0-windows', 'net8.0-windows net8.0'],
    ['NET8.0', 'net9.0 net8.0', 'net8.0 net9.0'],
    ['unknown', 'net8.0 netstandard2.0', 'net8.0 netstandard2.0']
  ]
  for (const [target, frameworks, expected] of cases) {
    const list = frameworks.split(' ')
    // frameworks that cannot stand in keep their order after those that can, as in OrderByNearest
    const ordered = [...list].sort((a, b) => (rank(target, a) - rank(target, b)) || (list.indexOf(a) - list.indexOf(b)))
    assert.deepStrictEqual(ordered.join(' '), expected, target)
  }
})

test('choose the content for a selection', () => {
  // The API's own content applies to net10.0 and net9.0, a variant to net8.0, another to netstandard2.0 and net462.
  const groups = [parseUnits('net10.0 net9.0'), parseUnits('net8.0'), parseUnits('netstandard2.0 net462')]

  assert.strictEqual(chooseGroup(groups, {}), 0)
  assert.strictEqual(chooseGroup(groups, { framework: 'net9.0' }), 0)
  assert.strictEqual(chooseGroup(groups, { framework: 'net8.0' }), 1)
  assert.strictEqual(chooseGroup(groups, { framework: 'net462' }), 2)
  // .NET Framework 4.7.2 is not documented: .NET Framework 4.6.2 stands in before .NET Standard 2.0, both in group 2
  assert.strictEqual(chooseGroup(groups, { framework: 'net472' }), 2)
  // .NET 11 is not documented: .NET 10 stands in
  assert.strictEqual(chooseGroup(groups, { framework: 'net11.0' }), 0)

  // An API on newer frameworks only does not exist on .NET Framework.
  assert.strictEqual(chooseGroup([parseUnits('net10.0 net9.0 net8.0')], { framework: 'net462' }), -1)
})

test('choose the content for an axis value', () => {
  // EF Core 3.1 builds for netstandard2.0 and net462, EF Core 8 and 9 for net8.0, EF Core 10 for net10.0.
  const own = parseUnits('ef10:net10.0 ef9:net8.0 ef8:net8.0')
  const ef3 = parseUnits('ef3:netstandard2.0 ef3:net462')

  assert.strictEqual(chooseGroup([own, ef3], { value: 'ef3' }), 1)
  assert.strictEqual(chooseGroup([own, ef3], { value: 'ef9' }), 0)
  // .NET 10 with EF Core 3.1: the .NET Standard 2.0 build stands in
  assert.strictEqual(chooseGroup([own, ef3], { value: 'ef3', framework: 'net10.0' }), 1)
  // EF Core 8 does not build for .NET Framework
  assert.strictEqual(chooseGroup([own, ef3], { value: 'ef8', framework: 'net462' }), -1)

  assert.ok(isAvailable(own, { value: 'ef8' }))
  assert.ok(!isAvailable(own, { value: 'ef3' }))

  // EF Core 3.1, picked on the EF Core pages, says nothing about APIs without that axis
  assert.ok(isAvailable(parseUnits('net10.0 net462'), { value: 'ef3' }))
  assert.strictEqual(chooseGroup([parseUnits('net10.0'), parseUnits('net462')], { value: 'ef3', framework: 'net472' }), 1)
})

test('frameworks in picker order', () => {
  assert.deepStrictEqual(
    sortFrameworks(['net462', 'netstandard2.0', 'net8.0', 'net10.0', 'net9.0', 'net8.0']),
    ['net10.0', 'net9.0', 'net8.0', 'netstandard2.0', 'net462'])
})

test('framework labels', () => {
  assert.strictEqual(frameworkLabel('net8.0'), '.NET 8')
  assert.strictEqual(frameworkLabel('net8.0-windows'), '.NET 8-windows')
  assert.strictEqual(frameworkLabel('netstandard2.0'), '.NET Standard 2.0')
  assert.strictEqual(frameworkLabel('net462'), '.NET Framework 4.6.2')
  assert.strictEqual(frameworkLabel('net462', { net462: '.NET Framework 4.6.2+' }), '.NET Framework 4.6.2+')
})

test('axis keys', () => {
  assert.strictEqual(axisKey({ name: 'EF Core', values: [] }), 'ef-core')
  assert.strictEqual(axisKey({ name: ' Driver! ', values: [] }), 'driver')
  // the frameworks have the key 'framework'
  assert.strictEqual(axisKey({ name: 'Framework', values: [] }), 'framework-axis')
})

test('valid frameworks', () => {
  assert.ok(isValidFramework('net8.0'))
  assert.ok(isValidFramework('netstandard2.0'))
  assert.ok(isValidFramework('net462'))
  assert.ok(!isValidFramework('net8'))
  assert.ok(!isValidFramework('typo'))
  // one docfx cannot compare is valid when the table of contents lists it
  assert.ok(!isValidFramework('uap10.0'))
  assert.ok(isValidFramework('uap10.0', ['uap10.0']))
})
