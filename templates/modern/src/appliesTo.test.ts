// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import test from 'node:test'
import assert from 'node:assert'
import { TemplateResult } from 'lit-html'
import {
  Axis, Selection, Toc, TocEntry, addHeadingAnchors, applyPrintSelection, applySelection, appliesToPicker, articleEntries, axisKey, canReference, chooseGroup, chooseGroups,
  distinguishCopies, entryDisplay, entryHref, findPath, frameworkLabel, getChoices, getSelection, hashId, initAppliesTo, isAvailable, isEntryShown, isHiddenBySelection,
  isNavEntryShown, keepLinkSelection, memberElements, narrowSelection, parseUnits, rank, scrollToShownTarget, select, shownEntries, shownTarget, sortFrameworks, unitsOf, validFramework,
  withSelectionCache
} from './appliesTo'
import { installDom } from './fakeDom'

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
  // names MSBuild does not write are no framework, as in TargetFrameworkHelper
  assert.ok(!canReference('net8.0', 'netstandard2'))
  assert.ok(!canReference('net8.0', 'netcoreapp3'))
  assert.ok(!canReference('netstandard2', 'netstandard2.0'))
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
    ['net8.0-windows', 'net8.0 net8.0-windows10.0.19041.0', 'net8.0-windows10.0.19041.0 net8.0'],
    ['net8.0-windows7.0', 'net8.0 net8.0-windows', 'net8.0-windows net8.0'],
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

test('a framework that leaves out the platform version picks the variant of that platform', () => {
  // The API's own content applies to net8.0, a variant to the Windows project, which loaded with its platform version.
  const groups = [parseUnits('net8.0'), parseUnits('net8.0-windows10.0.19041.0')]

  assert.strictEqual(chooseGroup(groups, { framework: 'net8.0-windows' }), 1)
  assert.strictEqual(chooseGroup([parseUnits('net8.0'), parseUnits('net8.0-windows')], { framework: 'net8.0-windows7.0' }), 1)
  assert.strictEqual(chooseGroup(groups, { framework: 'net8.0' }), 0)
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

test('a framework picked stands for the build of the part of the docs a project targeting it gets', () => {
  // a project for .NET 8 and .NET Standard 2.0: a project for .NET 8 gets the .NET 8 build, which lacks what only the
  // .NET Standard 2.0 build has
  const project = parseUnits('net8.0 netstandard2.0')
  assert.ok(!isAvailable(parseUnits('netstandard2.0'), { framework: 'net8.0', part: project }))
  assert.ok(isAvailable(parseUnits('netstandard2.0'), { framework: 'net10.0', part: parseUnits('netstandard2.0') }))
  assert.ok(isAvailable(parseUnits('net8.0'), { framework: 'net10.0', part: project }))
  assert.ok(isAvailable(parseUnits('netstandard2.0'), { framework: 'net462', part: project }))
  assert.strictEqual(chooseGroup([parseUnits('net8.0'), parseUnits('netstandard2.0')], { framework: 'net10.0', part: project }), 0)
  // where the part is not known, any build that can stand in does
  assert.ok(isAvailable(parseUnits('netstandard2.0'), { framework: 'net8.0' }))

  // each value of an axis is built for frameworks of its own: .NET 10 gets the .NET Standard 2.0 build of EF Core 3.1,
  // which lacks what only its .NET Framework 4.6.2 build has, and the .NET 10 build of EF Core 10
  const ef = parseUnits('ef3:netstandard2.0 ef3:net462 ef10:net10.0')
  assert.strictEqual(chooseGroup([parseUnits('ef10:net10.0'), parseUnits('ef3:netstandard2.0')], { value: 'ef3', framework: 'net10.0', part: ef }), 1)
  assert.ok(isAvailable(parseUnits('ef3:netstandard2.0'), { framework: 'net10.0', part: ef }))
  assert.ok(!isAvailable(parseUnits('ef3:net462'), { framework: 'net10.0', part: ef }))
  assert.ok(isAvailable(parseUnits('ef3:net462'), { framework: 'net472', part: ef }))
  // a framework no build of the part can stand in for has nothing of it
  assert.ok(!isAvailable(parseUnits('net8.0'), { framework: 'net462', part: parseUnits('net8.0 net10.0') }))
  // the platform version a framework leaves out is that of its build
  assert.ok(isAvailable(parseUnits('net8.0-windows10.0.19041.0'), { framework: 'net8.0-windows', part: parseUnits('net8.0 net8.0-windows10.0.19041.0') }))
  assert.ok(!isAvailable(parseUnits('net8.0'), { framework: 'net8.0-windows', part: parseUnits('net8.0 net8.0-windows10.0.19041.0') }))
})

test('one dimension picked keeps what differs in the other', () => {
  // Driver 1 differs between net8.0 and netstandard2.0; Driver 2 is the same on both but differs from Driver 1 on net8.0.
  const groups = [parseUnits('d1:net8.0'), parseUnits('d1:netstandard2.0'), parseUnits('d2:net8.0 d2:netstandard2.0')]

  // a value: the content of each of its frameworks, the API's own first
  assert.deepStrictEqual(chooseGroups(groups, { value: 'd1' }), [0, 1])
  assert.deepStrictEqual(chooseGroups(groups, { value: 'd2' }), [2])
  // a framework: the content of each value that stands in best for it
  assert.deepStrictEqual(chooseGroups(groups, { framework: 'net8.0' }), [0, 2])
  assert.deepStrictEqual(chooseGroups(groups, { framework: 'netstandard2.0' }), [1, 2])
  // .NET 10 has the net8.0 builds of both, not the netstandard2.0 one of Driver 1
  assert.deepStrictEqual(chooseGroups(groups, { framework: 'net10.0' }), [0, 2])
  assert.deepStrictEqual(chooseGroups(groups, { framework: 'net462' }), [1, 2])
  assert.deepStrictEqual(chooseGroups(groups, { framework: 'net45' }), [])
  // both: one
  assert.deepStrictEqual(chooseGroups(groups, { value: 'd1', framework: 'net10.0' }), [0])

  // without an axis, a framework has one content
  assert.deepStrictEqual(chooseGroups([parseUnits('net8.0'), parseUnits('netstandard2.0')], { framework: 'net10.0' }), [0])
  // the API's own content goes first when it is one of them, as it has no entry under "In other frameworks"
  assert.deepStrictEqual(chooseGroups([parseUnits('d1:netstandard2.0'), parseUnits('d2:net8.0')], { framework: 'net8.0' }), [0, 1])
})

test('a group kept beside the one in place is shown for the selection narrowed to it', () => {
  // the other dimension from the unit that stands in best: .NET 10 has the net8.0 build of Driver 2
  assert.deepStrictEqual(narrowSelection(parseUnits('d2:netstandard2.0 d2:net8.0'), { framework: 'net10.0' }), { framework: 'net10.0', value: 'd2' })
  assert.deepStrictEqual(narrowSelection(parseUnits('d1:netstandard2.0'), { value: 'd1' }), { framework: 'netstandard2.0', value: 'd1' })
  // a group the selection does not have is shown for the selection
  assert.deepStrictEqual(narrowSelection(parseUnits('d2:net8.0'), { value: 'd1' }), { value: 'd1' })
  // units that stand in equally well are all kept: the selection is limited to them, not to the first one
  const d1Old = narrowSelection(parseUnits('d1:netstandard2.0 d1:net462'), { value: 'd1' })
  assert.deepStrictEqual(d1Old, { value: 'd1', within: parseUnits('d1:netstandard2.0 d1:net462') })
  assert.deepStrictEqual(narrowSelection(parseUnits('d1:net8.0 d2:net8.0'), { framework: 'net10.0' }), { framework: 'net10.0', within: parseUnits('d1:net8.0 d2:net8.0') })
  assert.ok(isAvailable(parseUnits('d1:net462'), d1Old))
  assert.ok(!isAvailable(parseUnits('d1:net8.0'), d1Old))
  assert.ok(isAvailable(parseUnits('net462'), d1Old))
})

test('frameworks in picker order', () => {
  assert.deepStrictEqual(
    sortFrameworks(['net462', 'netstandard2.0', 'net8.0', 'net10.0', 'net9.0', 'net8.0']),
    ['net10.0', 'net9.0', 'net8.0', 'netstandard2.0', 'net462'])
})

test('framework labels', () => {
  assert.strictEqual(frameworkLabel('net8.0'), '.NET 8')
  assert.strictEqual(frameworkLabel('net8.0-windows'), '.NET 8 for Windows')
  assert.strictEqual(frameworkLabel('net8.0-windows10.0.19041.0'), '.NET 8 for Windows 10.0.19041.0')
  assert.strictEqual(frameworkLabel('net9.0-maccatalyst'), '.NET 9 for Mac Catalyst')
  assert.strictEqual(frameworkLabel('net8.0-newos1.0'), '.NET 8 for Newos 1.0')
  assert.strictEqual(frameworkLabel('netstandard2.0'), '.NET Standard 2.0')
  assert.strictEqual(frameworkLabel('net462'), '.NET Framework 4.6.2')
  assert.strictEqual(frameworkLabel('net462', { net462: '.NET Framework 4.6.2+' }), '.NET Framework 4.6.2+')
  // as the page's own Applies to line, which ManagedReference.common.js writes
  assert.strictEqual(frameworkLabel('net8.0', { 'NET8.0': 'Modern' }), 'Modern')
  // a link spells a framework in any case, which is its label until the table of contents spells it
  assert.strictEqual(frameworkLabel('NETSTANDARD2.0'), '.NET Standard 2.0')
  assert.strictEqual(frameworkLabel('NetCoreApp3.1'), '.NET Core 3.1')
  assert.strictEqual(frameworkLabel('NET8.0'), '.NET 8')
  assert.strictEqual(frameworkLabel('NET462'), '.NET Framework 4.6.2')
})

test('axis keys', () => {
  assert.strictEqual(axisKey({ name: 'EF Core', values: [] }), 'ef-core')
  assert.strictEqual(axisKey({ name: ' Driver! ', values: [] }), 'driver')
  // the frameworks have the key 'framework', a link to tabs 'tabs', a search result 'q'
  assert.strictEqual(axisKey({ name: 'Framework', values: [] }), 'framework_axis')
  assert.strictEqual(axisKey({ name: 'Tabs', values: [] }), 'tabs_axis')
  assert.strictEqual(axisKey({ name: 'Q', values: [] }), 'q_axis')
  // which no other name has: a name gives no underscore
  assert.strictEqual(axisKey({ name: 'Framework Axis', values: [] }), 'framework-axis')
  assert.strictEqual(axisKey({ name: 'Framework_Axis', values: [] }), 'framework-axis')
  assert.strictEqual(axisKey({ name: 'Axis', values: [] }), 'axis')
  // the letters of any script, and a key for a name without any
  assert.strictEqual(axisKey({ name: 'Версия EF', values: [] }), 'версия-ef')
  assert.strictEqual(axisKey({ name: '数据库驱动', values: [] }), '数据库驱动')
  assert.strictEqual(axisKey({ name: '?!', values: [] }), '_axis')
})

test('valid frameworks', () => {
  // before the table of contents is loaded, any framework docfx can compare
  assert.strictEqual(validFramework('net8.0'), 'net8.0')
  assert.strictEqual(validFramework('netstandard2.0'), 'netstandard2.0')
  assert.strictEqual(validFramework('net462'), 'net462')
  assert.strictEqual(validFramework('net8'), undefined)
  assert.strictEqual(validFramework('typo'), undefined)
  assert.strictEqual(validFramework('uap10.0'), undefined)

  // then only one it lists, as it spells it: .NET Framework 4.6 would leave out every API of this one
  const listed = ['net10.0', 'netstandard2.0', 'net462']
  assert.strictEqual(validFramework('NET10.0', listed), 'net10.0')
  assert.strictEqual(validFramework('net46', listed), undefined)
  assert.strictEqual(validFramework('netstandard1.0', listed), undefined)
  assert.strictEqual(validFramework('uap10.0', ['uap10.0']), 'uap10.0')
  // not one a listed framework stands in for: the reader would see .NET Standard 2.0 labelled .NET Framework 4.8
  assert.strictEqual(validFramework('net48', listed), undefined)
})

const site = `
  <meta name="docfx:appliestorel" content="../">
  <meta name="loc:appliesToUnavailable" content="Not available in {selection}">`
const ef: Axis = { name: 'EF Core', values: [{ id: 'ef3', label: 'EF Core 3.1' }, { id: 'ef9', label: 'EF Core 9' }] }
const driver: Axis = { name: 'Driver', values: [{ id: 'd1', label: 'Driver 1' }, { id: 'd2', label: 'Driver 2' }] }
const frameworks = ['net10.0', 'net8.0', 'netstandard2.0', 'net462']

test('selection from a link, else from what was picked last', () => {
  const { storage } = installDom(site, 'https://example.org/docs/api/A.html?framework=net46&ef-core=ef9')
  // the key includes the root of the site, which shares storage with the other sites of its origin
  storage.set('docfx.axis./docs/.framework', 'net8.0')
  storage.set('docfx.axis./docs/.ef-core', 'ef3')

  // the link's framework is not one of the table of contents, so the one picked last stays
  assert.deepStrictEqual(getSelection(ef, frameworks), { framework: 'net8.0', value: 'ef9' })
  // a page without the axis has no value
  assert.deepStrictEqual(getSelection(undefined, frameworks), { framework: 'net8.0', value: undefined })

  // a framework kept from another part of the site that this table of contents does not list is left out, but kept,
  // for that part of the site
  storage.set('docfx.axis./docs/.framework', 'net6.0')
  assert.deepStrictEqual(getSelection(ef, frameworks), { framework: undefined, value: 'ef9' })
  assert.strictEqual(storage.get('docfx.axis./docs/.framework'), 'net6.0')
  // before the table of contents is loaded, any framework docfx can compare; on a page without a link's framework,
  // which would come first
  const later = installDom(site, 'https://example.org/docs/api/A.html')
  later.storage.set('docfx.axis./docs/.framework', 'net6.0')
  assert.deepStrictEqual(getSelection(ef).framework, 'net6.0')
  getSelection(ef, [])
  assert.strictEqual(later.storage.get('docfx.axis./docs/.framework'), 'net6.0')
  // so is a value that is not one of the axis
  installDom(site, 'https://example.org/docs/api/A.html?ef-core=ef99')
  assert.deepStrictEqual(getSelection(ef, frameworks), { framework: undefined, value: undefined })
})

test('the table of contents reads the selection once per render', () => {
  const { storage } = installDom(site, 'https://example.org/docs/api/A.html')
  storage.set('docfx.axis./docs/.ef-core', 'ef3')

  withSelectionCache(() => {
    assert.strictEqual(getSelection(ef, frameworks).value, 'ef3')
    storage.set('docfx.axis./docs/.ef-core', 'ef9')
    assert.strictEqual(getSelection(ef, frameworks).value, 'ef3')
    // each axis has its own
    assert.strictEqual(getSelection(driver, frameworks).value, undefined)
  })
  // and the next render reads it again
  assert.strictEqual(getSelection(ef, frameworks).value, 'ef9')
})

test('a link keeps a valid choice for the next pages', () => {
  const { storage } = installDom(site, 'https://example.org/docs/api/A.html?framework=NET8.0&ef-core=ef99')
  keepLinkSelection({ frameworks, axis: ef, key: 'ef-core' })
  assert.strictEqual(storage.get('docfx.axis./docs/.framework'), 'net8.0')
  assert.ok(!storage.has('docfx.axis./docs/.ef-core'))

  const next = installDom(site, 'https://example.org/docs/api/A.html?framework=net46&ef-core=ef3').storage
  keepLinkSelection({ frameworks, axis: ef, key: 'ef-core' })
  assert.ok(!next.has('docfx.axis./docs/.framework'))
  assert.strictEqual(next.get('docfx.axis./docs/.ef-core'), 'ef3')

  // the value of the axis of another part of the docs too, whose entries the table of contents filters by it
  const other = installDom(site, 'https://example.org/docs/api/Guide.html?ef-core=ef3&driver=d9').storage
  keepLinkSelection({ frameworks, axes: [ef, driver] })
  assert.strictEqual(other.get('docfx.axis./docs/.ef-core'), 'ef3')
  assert.ok(!other.has('docfx.axis./docs/.driver'))
})

test('a link page reloaded or gone back to keeps what was picked since', () => {
  // the link picked .NET Framework 4.6.2, then the reader picked .NET 8 on another page
  for (const navigation of ['reload', 'back_forward'] as const) {
    const { storage } = installDom(site, 'https://example.org/docs/api/A.html?framework=net462&ef-core=ef3', navigation)
    storage.set('docfx.axis./docs/.framework', 'net8.0')
    keepLinkSelection({ frameworks, axis: ef, key: 'ef-core' })
    assert.strictEqual(storage.get('docfx.axis./docs/.framework'), 'net8.0', navigation)
    assert.ok(!storage.has('docfx.axis./docs/.ef-core'), navigation)
    // the page itself still shows what its address names
    assert.deepStrictEqual(getSelection(ef, frameworks), { framework: 'net462', value: 'ef3' }, navigation)
  }
})

// A table of contents including two API tables of contents, each with its axis on its entries, and an article.
function mergedToc(): Toc {
  return {
    items: [
      {
        name: 'EF',
        axis: ef,
        items: [
          { name: 'Everywhere', href: 'ef/Everywhere.html', appliesTo: [{ version: 'ef3', frameworks: ['netstandard2.0'] }, { version: 'ef9', frameworks: ['net8.0'] }] },
          { name: 'OnlyEf9', href: 'ef/OnlyEf9.html', appliesTo: [{ version: 'ef9', frameworks: ['net8.0'] }] }
        ]
      },
      {
        name: 'Drivers',
        axis: driver,
        items: [
          { name: 'OnlyD1', href: 'drivers/OnlyD1.html', appliesTo: [{ version: 'd1', frameworks: ['net10.0', 'net462'] }] },
          { name: 'OnlyD2', href: 'drivers/OnlyD2.html', appliesTo: [{ version: 'd2', frameworks: ['net10.0'] }] }
        ]
      },
      { name: 'Guide', href: 'guide.html' }
    ]
  }
}

test('the axis of the part of the docs a page is in', () => {
  installDom(site, 'https://example.org/docs/api/A.html')
  const toc = mergedToc()
  const tocUrl = new URL('https://example.org/docs/toc.json')
  const choicesAt = (path: string) => getChoices(toc, findPath(toc.items, tocUrl, { pathname: path }))

  assert.strictEqual(choicesAt('/docs/ef/OnlyEf9.html').axis, ef)
  assert.strictEqual(choicesAt('/docs/ef/OnlyEf9.html').key, 'ef-core')
  assert.strictEqual(choicesAt('/docs/drivers/OnlyD2.html').axis, driver)
  // an article of neither part has no axis when there are two
  assert.strictEqual(choicesAt('/docs/guide.html').axis, undefined)
  assert.deepStrictEqual(choicesAt('/docs/guide.html').frameworks, ['net10.0', 'net8.0', 'netstandard2.0', 'net462'])

  // with one, an article has it, such as the index of the API
  const single = { items: [mergedToc().items[0], { name: 'Index', href: 'index.html' }] }
  assert.strictEqual(getChoices(single, findPath(single.items, tocUrl, { pathname: '/docs/index.html' })).axis, ef)

  // an API page of a part without an axis has none, even when another part has the only one
  const withCore: Toc = {
    items: [
      { name: 'Core', items: [{ name: 'Db', href: 'core/Db.html', appliesTo: [{ frameworks: ['net10.0', 'net462'] }] }] },
      mergedToc().items[0],
      { name: 'Index', href: 'index.html' }
    ]
  }
  const axisAt = (path: string) => getChoices(withCore, findPath(withCore.items, tocUrl, { pathname: path })).axis
  assert.strictEqual(axisAt('/docs/core/Db.html'), undefined)
  assert.strictEqual(axisAt('/docs/ef/OnlyEf9.html'), ef)
  assert.strictEqual(axisAt('/docs/index.html'), ef)
  assert.strictEqual(axisAt('/docs/elsewhere.html'), ef)

  // the table of contents is read once
  assert.strictEqual(getChoices(toc).frameworks, getChoices(toc).frameworks)
})

test('parts of the docs with an axis of the same name offer the values of all of them', () => {
  const ef10: Axis = { name: 'EF Core', values: [{ id: 'ef9', label: 'EF Core 9' }, { id: 'ef10', label: 'EF Core 10' }] }
  const toc: Toc = {
    items: [
      mergedToc().items[0],
      { name: 'EF 10', axis: ef10, items: [{ name: 'OnlyEf10', href: 'ef10/OnlyEf10.html', appliesTo: [{ version: 'ef10', frameworks: ['net10.0'] }] }] },
      { name: 'Guide', href: 'guide.html' }
    ]
  }
  const tocUrl = new URL('https://example.org/docs/toc.json')
  const choices = getChoices(toc, findPath(toc.items, tocUrl, { pathname: '/docs/guide.html' }))
  assert.deepStrictEqual(choices.axis.values.map(v => v.id), ['ef3', 'ef9', 'ef10'])
  // the axes of the parts themselves are left as they are
  assert.deepStrictEqual(ef.values.map(v => v.id), ['ef3', 'ef9'])
  assert.strictEqual(getChoices(toc, findPath(toc.items, tocUrl, { pathname: '/docs/ef10/OnlyEf10.html' })).axis, ef10)

  // a link to an article keeps a value only the second part has (the storage key has the root of the site the page names)
  const { storage } = installDom(site, 'https://example.org/docs/api/Guide.html?ef-core=ef10')
  keepLinkSelection(choices)
  assert.strictEqual(storage.get('docfx.axis./docs/.ef-core'), 'ef10')

  // names that share a key, such as in another letter case, are one axis too: they would share what is kept
  const efCore: Axis = { name: 'EF core', values: [{ id: 'ef10', label: 'EF Core 10' }] }
  const cased: Toc = { items: [mergedToc().items[0], { name: 'EF 10', axis: efCore, items: [] }, { name: 'Guide', href: 'guide.html' }] }
  const casedChoices = getChoices(cased, findPath(cased.items, tocUrl, { pathname: '/docs/guide.html' }))
  assert.strictEqual(casedChoices.axes.filter(a => axisKey(a) === 'ef-core').length, 1)
  assert.deepStrictEqual(casedChoices.axis.values.map(v => v.id), ['ef3', 'ef9', 'ef10'])
})

test('the table of contents shows what exists in the selection', () => {
  const { storage } = installDom(site, 'https://example.org/docs/api/A.html')
  const toc = mergedToc()
  const names = (selection: Selection, keep: (entry: TocEntry) => boolean = () => false) =>
    shownEntries(toc.items, selection, frameworks, keep).map(e => e.name)

  assert.deepStrictEqual(names({}), ['Everywhere', 'OnlyEf9', 'OnlyD1', 'OnlyD2', 'Guide'])
  // .NET Framework 4.6.2 can use the .NET Standard 2.0 build of EF Core 3.1
  assert.deepStrictEqual(names({ framework: 'net462' }), ['Everywhere', 'OnlyD1', 'Guide'])

  // each part has the value picked for its own axis
  storage.set('docfx.axis./docs/.ef-core', 'ef3')
  assert.deepStrictEqual(names({}), ['Everywhere', 'OnlyD1', 'OnlyD2', 'Guide'])
  storage.set('docfx.axis./docs/.driver', 'd2')
  assert.deepStrictEqual(names({}), ['Everywhere', 'OnlyD2', 'Guide'])

  // the page being read and its parents stay
  assert.deepStrictEqual(names({}, entry => entry.name === 'OnlyD1' || entry.name === 'Drivers'), ['Everywhere', 'OnlyD1', 'OnlyD2', 'Guide'])
})

test('a group of entries the selection all leaves out goes too', () => {
  const { storage } = installDom(site, 'https://example.org/docs/api/A.html')
  const providers: TocEntry = {
    name: 'Providers',
    items: [{ name: 'Sql', href: 'Sql.html', appliesTo: [{ frameworks: ['net8.0'] }] }, { name: 'Ole', href: 'Ole.html', appliesTo: [{ frameworks: ['net462'] }] }]
  }
  // a namespace with a page of its own, whose types are on .NET 8 only
  const ns: TocEntry = { name: 'Lib.Modern', href: 'Lib.Modern.html', items: [{ name: 'Span', href: 'Span.html', appliesTo: [{ frameworks: ['net8.0'] }] }] }
  // the groups of the members of a type with a page each, which have no frameworks of their own
  const type: TocEntry = {
    name: 'Shared',
    href: 'Shared.html',
    appliesTo: [{ frameworks: ['net8.0', 'net462'] }],
    items: [{ name: 'Methods', items: [{ name: 'Do', href: 'Do.html', appliesTo: [{ frameworks: ['net8.0'] }] }] }]
  }
  const display = (entry: TocEntry, selection: Selection, keep: (entry: TocEntry) => boolean = () => false) =>
    entryDisplay(entry, selection, frameworks, keep)

  assert.strictEqual(display(providers, {}), 'group')
  assert.strictEqual(display(providers, { framework: 'net462' }), 'group')
  assert.strictEqual(display(providers, { framework: 'net45' }), 'none')
  assert.strictEqual(display(ns, { framework: 'net462' }), 'leaf')
  assert.strictEqual(display(type, { framework: 'net462' }), 'leaf')
  assert.strictEqual(display(type.items[0], { framework: 'net462' }), 'none')
  assert.strictEqual(display(type, { framework: 'net8.0' }), 'group')
  // the page being read stays where it is
  assert.strictEqual(display(type, { framework: 'net462' }, entry => entry.name === 'Do' || entry.name === 'Methods'), 'group')
  // a part with the value picked for its axis: Driver 2 has no .NET Framework build
  const drivers = mergedToc().items[1]
  assert.strictEqual(display(drivers, { framework: 'net462' }), 'group')
  storage.set('docfx.axis./docs/.driver', 'd2')
  assert.strictEqual(display(drivers, { framework: 'net462' }), 'none')
})

test('a namespace the selection leaves out is the heading of the namespaces in it that it keeps', () => {
  installDom(site, 'https://example.org/docs/api/A.html')
  // `namespaceLayout: nested`: the types of Lib are on .NET 8 only, those of Lib.Data and Lib.Data.Ef everywhere;
  // Lib.Data has no types, so no frameworks, of its own
  const lib: TocEntry = {
    name: 'Lib',
    href: 'Lib.html',
    appliesTo: [{ frameworks: ['net8.0'] }],
    items: [
      { name: 'Span', href: 'Lib.Span.html', appliesTo: [{ frameworks: ['net8.0'] }] },
      {
        name: 'Data',
        items: [{ name: 'Ef', href: 'Lib.Data.Ef.html', appliesTo: [{ frameworks: ['net8.0', 'netstandard2.0'] }] }]
      },
      { name: 'Guide', href: 'guide.html' }
    ]
  }
  const none = () => false
  assert.strictEqual(entryDisplay(lib, { framework: 'net8.0' }, frameworks, none), 'group')
  assert.strictEqual(entryDisplay(lib, { framework: 'netstandard2.0' }, frameworks, none), 'heading')
  // without its page, nor the entries below it that do not exist in the selection
  assert.deepStrictEqual(shownEntries([lib], { framework: 'netstandard2.0' }, frameworks, none).map(e => e.name), ['Ef', 'Guide'])
  // an entry without frameworks of its own, such as an article, does not keep it
  const guideOnly: TocEntry = { ...lib, items: [lib.items[0], lib.items[2]] }
  assert.strictEqual(entryDisplay(guideOnly, { framework: 'netstandard2.0' }, frameworks, none), 'none')
  assert.deepStrictEqual(shownEntries([guideOnly], { framework: 'netstandard2.0' }, frameworks, none), [])

  // the heading has no link to its page, which has nothing for the selection; shown whole, it has
  assert.strictEqual(entryHref(lib, entryDisplay(lib, { framework: 'netstandard2.0' }, frameworks, none) as 'heading'), undefined)
  assert.strictEqual(entryHref(lib, 'group'), 'Lib.html')
  assert.strictEqual(entryHref(lib.items[0], 'leaf'), 'Lib.Span.html')
})

test('the table of contents leaves out what the build of its part for the framework picked lacks', () => {
  installDom(site, 'https://example.org/docs/api/A.html')
  const net8 = { frameworks: ['net8.0', 'netstandard2.0'] }
  // a part for .NET 8 and .NET Standard 2.0, whose Legacy type only the .NET Standard 2.0 build has, nor its members'
  // category, which is no part of its own; and a part for .NET Standard 2.0 only
  const toc: Toc = {
    items: [
      {
        name: 'Core',
        items: [{
          name: 'Lib',
          href: 'Lib.html',
          type: 'Namespace',
          appliesTo: [net8],
          items: [
            { name: 'Shared', href: 'Lib.Shared.html', type: 'Class', appliesTo: [net8] },
            {
              name: 'Legacy',
              href: 'Lib.Legacy.html',
              type: 'Class',
              appliesTo: [{ frameworks: ['netstandard2.0'] }],
              items: [{ name: 'Methods', items: [{ name: 'Run', href: 'Lib.Legacy.Run.html', type: 'Method', appliesTo: [{ frameworks: ['netstandard2.0'] }] }] }]
            }
          ]
        }]
      },
      { name: 'Old', items: [{ name: 'Old', href: 'Old.html', type: 'Namespace', appliesTo: [{ frameworks: ['netstandard2.0'] }] }] }
    ]
  }
  const { frameworks: listed } = getChoices(toc)
  const names = (framework: string) => shownEntries(toc.items, { framework }, listed, () => false).map(e => e.name)

  assert.deepStrictEqual(names('net8.0'), ['Lib', 'Shared', 'Old'])
  assert.deepStrictEqual(names('netstandard2.0'), ['Lib', 'Shared', 'Legacy', 'Run', 'Old'])
  // "Next article" goes through the same entries
  const { storage } = installDom(`${site}<meta name="docfx:appliestopicker" content="true">`, 'https://example.org/docs/api/A.html')
  storage.set('docfx.axis./docs/.framework', 'net8.0')
  assert.deepStrictEqual(articleEntries(toc, []).map(e => e.name), ['Lib', 'Shared', 'Old'])
  // a page knows the part it is in
  const tocUrl = new URL('https://example.org/docs/toc.json')
  assert.deepStrictEqual(getChoices(toc, findPath(toc.items, tocUrl, { pathname: '/docs/Old.html' })).part, parseUnits('netstandard2.0'))
  assert.deepStrictEqual(getChoices(toc, findPath(toc.items, tocUrl, { pathname: '/docs/Lib.Legacy.html' })).part, parseUnits('net8.0 netstandard2.0'))
})

test('an entry written with an empty appliesTo says nothing of its frameworks', () => {
  installDom(site, 'https://example.org/docs/api/A.html')
  // as an entry without `appliesTo`, such as an article: shown for any selection, and not the API of a heading
  const empty: TocEntry = { name: 'Notes', href: 'notes.html', appliesTo: [] }
  assert.strictEqual(isEntryShown(empty, { framework: 'netstandard2.0' }), true)
  assert.strictEqual(entryDisplay(empty, { framework: 'netstandard2.0' }, frameworks, () => false), 'leaf')
  const ns: TocEntry = { name: 'Lib', href: 'Lib.html', appliesTo: [{ frameworks: ['net8.0'] }], items: [empty] }
  assert.strictEqual(entryDisplay(ns, { framework: 'netstandard2.0' }, frameworks, () => false), 'none')
  assert.deepStrictEqual(shownEntries([{ name: 'Root', items: [empty] }], { framework: 'netstandard2.0' }, frameworks, () => false).map(e => e.name), ['Notes'])
})

const page = `${site}
  <article>
    <h1 id="Lib_Shared" data-applies-to="net8.0 netstandard2.0">Shared</h1>
    <div class="facts"></div>
    <h3 id="Lib_Shared_Size" data-applies-to="net8.0 netstandard2.0">Size</h3>
    <div class="api-contents">
      <div class="api-content" data-applies-to="net8.0"><h4 id="Lib_Shared_Size_remarks">int</h4></div>
      <div class="api-content" data-applies-to="netstandard2.0" data-search-ignore hidden><h4 id="Lib_Shared_Size_variant1_remarks">long</h4></div>
    </div>
    <h6 class="api-variants-heading">In other frameworks</h6>
    <div class="api-variant" data-applies-to="netstandard2.0">long</div>
    <a id="Lib_Shared_Do_" data-overload></a>
    <h3 id="Lib_Shared_Do" data-applies-to="net8.0">Do</h3>
    <p>Only on .NET 8.</p>
    <a id="Lib_Shared_Run_" data-overload></a>
    <h3 id="Lib_Shared_Run" data-applies-to="net8.0 netstandard2.0">Run</h3>
    <dl class="parameters">
      <dt id="Lib_Mode_Strict" data-applies-to="net8.0 netstandard2.0"><span class="api-contents"><span class="api-content" data-applies-to="net8.0"><code>Strict = 2</code></span><span class="api-content" data-applies-to="netstandard2.0" data-search-ignore hidden><code>Strict = 1</code></span></span></dt>
      <dd>Strict</dd>
      <dt id="Lib_Mode_Fast" data-applies-to="net8.0"><code>Fast = 1</code></dt>
      <dd>Fast</dd>
    </dl>
  </article>`

test('the page shows the content for the selection', () => {
  const { document } = installDom(page)
  const byId = (id: string) => document.getElementById(id)
  const shown = (id: string) => !byId(id).closest('[hidden]')

  applySelection({ framework: 'netstandard2.0' })
  // each API shows its content for the framework, a member and the value of an enum alike
  assert.ok(!shown('Lib_Shared_Size_remarks'))
  assert.ok(shown('Lib_Shared_Size_variant1_remarks'))
  assert.deepStrictEqual(document.querySelectorAll('dt code').filter(c => !c.closest('[hidden]')).map(c => c.textContent), ['Strict = 1', 'Fast = 1'])
  // without what differs elsewhere
  assert.ok(document.querySelectorAll('.api-variants-heading, .api-variant').every(e => e.hidden))
  // a member that does not exist in it is dimmed and marked, up to the next overload
  assert.ok(byId('Lib_Shared_Do').classList.contains('applies-to-unavailable'))
  assert.strictEqual(byId('Lib_Shared_Do').querySelector('.applies-to-unavailable-badge').textContent, 'Not available in .NET Standard 2.0')
  assert.ok(!byId('Lib_Shared_Run_').classList.contains('applies-to-unavailable'))
  assert.ok(byId('Lib_Mode_Fast').classList.contains('applies-to-unavailable'))
  assert.ok(!byId('Lib_Mode_Strict').classList.contains('applies-to-unavailable'))
  assert.strictEqual(document.querySelector('.applies-to-banner'), null)

  // a framework the type does not exist in says so
  applySelection({ framework: 'netcoreapp1.0' })
  assert.strictEqual(document.querySelector('.facts').nextElementSibling.textContent, 'Not available in .NET Core 1.0')
  assert.strictEqual(document.querySelectorAll('.applies-to-banner').length, 1)

  // "All" shows the page as without a selection
  applySelection({})
  assert.ok(shown('Lib_Shared_Size_remarks'))
  assert.ok(!shown('Lib_Shared_Size_variant1_remarks'))
  assert.ok(document.querySelectorAll('.api-variants-heading, .api-variant').every(e => !e.hidden))
  assert.deepStrictEqual(document.querySelectorAll('.applies-to-unavailable, .applies-to-unavailable-badge, .applies-to-banner'), [])
})

test('the title of a type that is an interface in some frameworks names what is shown', () => {
  const { document } = installDom(`${site}
  <article>
    <h1 id="Lib_Shape" data-applies-to="net8.0 netstandard2.0"><span class="api-contents"><span class="api-content" data-applies-to="net8.0">Class Shape</span><span class="api-content" data-applies-to="netstandard2.0" data-search-ignore hidden>Interface Shape</span></span></h1>
    <div class="facts"></div>
    <div class="api-contents">
      <div class="api-content" data-applies-to="net8.0"><p id="own">own</p></div>
      <div class="api-content" data-applies-to="netstandard2.0" data-search-ignore hidden><p id="copy">copy</p></div>
    </div>
    <h6 class="api-variants-heading">In other frameworks</h6>
    <div class="api-variant" data-applies-to="netstandard2.0">interface</div>
  </article>`)
  const title = () => document.querySelectorAll('h1 .api-content').filter(e => !e.hidden).map(e => e.textContent)

  applySelection({ framework: 'netstandard2.0' })
  assert.deepStrictEqual(title(), ['Interface Shape'])
  assert.ok(document.getElementById('copy').closest('[hidden]') === null)
  // and the title does not take the place of what "In other frameworks" lists
  assert.ok(document.querySelector<HTMLElement>('.api-variant').hidden)

  applySelection({})
  assert.deepStrictEqual(title(), ['Class Shape'])
  assert.ok(!document.querySelector<HTMLElement>('.api-variant').hidden)
})

test('one dimension picked keeps what differs in the other on the page', () => {
  // Driver 1 differs between net8.0 and netstandard2.0; Driver 2 is the same on both, and its summary is the API's own
  const { document } = installDom(`${site}
  <article>
    <h1 id="Lib_Reader" data-applies-to="d1:net8.0 d1:netstandard2.0 d2:net8.0 d2:netstandard2.0">Reader</h1>
    <div class="facts"></div>
    <div class="api-contents">
      <div class="api-content" data-applies-to="d1:net8.0"><p id="own">own</p></div>
      <div class="api-content" data-applies-to="d1:netstandard2.0" hidden><p id="copy1">copy1</p></div>
      <div class="api-content" data-applies-to="d2:net8.0 d2:netstandard2.0" hidden><p id="copy2">copy2</p></div>
    </div>
    <h5 class="api-variants-heading">In other frameworks</h5>
    <div class="api-variant" id="variant1" data-applies-to="d1:netstandard2.0"><h6 class="api-variant-label">Driver 1</h6><div id="diff1">summary</div></div>
    <div class="api-variant" id="variant2" data-applies-to="d2:net8.0 d2:netstandard2.0"><h6 class="api-variant-label">Driver 2</h6><div id="diff2">declaration</div></div>
  </article>`)
  const hidden = (...ids: string[]) => ids.map(id => !!document.getElementById(id).closest('[hidden]'))
  const heading = () => document.querySelector<HTMLElement>('.api-variants-heading').hidden
  const all = ['own', 'copy1', 'copy2', 'variant1', 'variant2', 'diff1', 'diff2']
  const contents = () => document.querySelector('.api-contents').children.map(e => e.children[0].id)

  applySelection({ value: 'd1' })
  assert.deepStrictEqual(hidden(...all), [false, true, true, false, true, false, true])
  assert.ok(!heading())

  applySelection({ framework: 'net8.0' })
  assert.deepStrictEqual(hidden(...all), [false, true, true, true, false, true, false])

  // Driver 1 on .NET Standard 2.0 is shown in place of the API's own content, which what Driver 2's entry lists differs
  // from: the entry shows Driver 2 whole instead, below its label
  applySelection({ framework: 'netstandard2.0' })
  assert.deepStrictEqual(hidden(...all), [true, false, false, true, false, true, true])
  assert.strictEqual(document.getElementById('copy2').closest('.api-variant').id, 'variant2')
  assert.strictEqual(document.getElementById('variant2').children[1].children[0].id, 'copy2')
  assert.ok(!heading())

  // Driver 2 is shown in place of the API's own content, so not under "In other frameworks" too; its copy is back
  applySelection({ value: 'd2' })
  assert.deepStrictEqual(hidden(...all), [true, true, false, true, true, true, true])
  assert.deepStrictEqual(contents(), ['own', 'copy1', 'copy2'])
  assert.ok(heading())

  applySelection({ value: 'd1', framework: 'net8.0' })
  assert.deepStrictEqual(hidden(...all), [false, true, true, true, true, true, true])
  assert.ok(heading())

  applySelection({ framework: 'netstandard2.0' })
  applySelection({})
  assert.deepStrictEqual(hidden(...all), [false, true, true, false, false, false, false])
  assert.deepStrictEqual(contents(), ['own', 'copy1', 'copy2'])
  assert.strictEqual(document.getElementById('copy2').closest('.api-variant'), null)
  assert.ok(!heading())
})

test('a variant shown in place shows the others whole, and a link goes to the one in place', () => {
  // Driver 1 on .NET 10 differs from the API's own content, which is Driver 1 on .NET 8; Driver 2 is on .NET Standard 2.0
  const { document } = installDom(`${site}
  <article>
    <h1 id="Lib_Reader" data-applies-to="d1:net8.0 d2:netstandard2.0 d1:net10.0">Reader</h1>
    <div class="facts"></div>
    <div class="api-contents">
      <div class="api-content" data-applies-to="d1:net8.0"><p id="text">own</p></div>
      <div class="api-content" data-applies-to="d2:netstandard2.0" hidden><p id="text_variant1">Driver 2</p></div>
      <div class="api-content" data-applies-to="d1:net10.0" hidden><p id="text_variant2">Driver 1 on .NET 10</p></div>
    </div>
    <h5 class="api-variants-heading">In other frameworks</h5>
    <div class="api-variant" id="variant1" data-applies-to="d2:netstandard2.0"><h6 class="api-variant-label">Driver 2</h6><div id="diff1">summary</div></div>
    <div class="api-variant" id="variant2" data-applies-to="d1:net10.0"><h6 class="api-variant-label">Driver 1</h6><div id="diff2">declaration</div></div>
  </article>`)
  const hidden = (...ids: string[]) => ids.map(id => !!document.getElementById(id).closest('[hidden]'))

  applySelection({ framework: 'net10.0' })
  // Driver 1 on .NET 10 in place, Driver 2 whole in its entry, which comes before it on the page
  assert.deepStrictEqual(hidden('text', 'text_variant1', 'text_variant2', 'variant1', 'diff1', 'variant2'), [true, false, false, false, true, true])
  assert.strictEqual(document.getElementById('text_variant1').closest('.api-variant').id, 'variant1')
  assert.strictEqual(shownTarget('text').id, 'text_variant2')

  applySelection({})
  assert.deepStrictEqual(document.querySelector('.api-contents').children.map(e => e.children[0].id), ['text', 'text_variant1', 'text_variant2'])
  assert.deepStrictEqual(hidden('text', 'text_variant1', 'text_variant2', 'variant1', 'diff1', 'variant2'), [false, true, true, false, false, false])
})

test('a copy shown whole shows what it holds as it is in the frameworks of its variant', () => {
  // An enum whose description differs for Driver 1 between .NET 8 and .NET Standard 2.0, and whose copy for Driver 2
  // lists the values again, as the API has them: Fast is described for each driver, Slow exists for Driver 1 only.
  const type = (suffix: string) => `
    <p id="own${suffix}">own</p>
    <dl class="parameters">
      <dt id="Lib_Mode_Fast${suffix}" data-applies-to="d1:net8.0 d1:netstandard2.0 d2:netstandard2.0">Fast</dt>
      <dd>
        <div class="api-contents">
          <div class="api-content" data-applies-to="d1:net8.0 d1:netstandard2.0"><p id="fast${suffix}">Driver 1</p></div>
          <div class="api-content" data-applies-to="d2:netstandard2.0" data-search-ignore hidden><p id="fast${suffix}_d2">Driver 2</p></div>
        </div>
        <h5 class="api-variants-heading">In other frameworks</h5>
        <div class="api-variant" id="fastEntry${suffix}" data-applies-to="d2:netstandard2.0"><h6 class="api-variant-label">Driver 2</h6><div>summary</div></div>
      </dd>
      <dt id="Lib_Mode_Slow${suffix}" data-applies-to="d1:net8.0 d1:netstandard2.0">Slow</dt>
      <dd>slow</dd>
    </dl>`
  const { document } = installDom(`${site}
  <article>
    <h1 id="Lib_Mode" data-applies-to="d1:net8.0 d1:netstandard2.0 d2:netstandard2.0">Mode</h1>
    <div class="facts"></div>
    <div class="api-contents">
      <div class="api-content" data-applies-to="d1:net8.0">${type('')}</div>
      <div class="api-content" data-applies-to="d1:netstandard2.0" data-search-ignore hidden>${type('_variant1')}</div>
      <div class="api-content" data-applies-to="d2:netstandard2.0" data-search-ignore hidden>${type('_variant2')}</div>
    </div>
    <h5 class="api-variants-heading">In other frameworks</h5>
    <div class="api-variant" id="variant1" data-applies-to="d1:netstandard2.0"><h6 class="api-variant-label">Driver 1</h6><div>summary</div></div>
    <div class="api-variant" id="variant2" data-applies-to="d2:netstandard2.0"><h6 class="api-variant-label">Driver 2</h6><div>summary</div></div>
  </article>`)
  const hidden = (...ids: string[]) => ids.map(id => !!document.getElementById(id).closest('[hidden]'))
  const badge = (id: string) => document.getElementById(id).querySelector('.applies-to-unavailable-badge')?.textContent

  // Driver 1 on .NET Standard 2.0 in place, Driver 2 whole in its entry
  applySelection({ framework: 'netstandard2.0' }, driver)
  assert.strictEqual(document.getElementById('own_variant2').closest('.api-variant').id, 'variant2')
  // in place, Fast keeps Driver 2's description under its own "In other frameworks"
  assert.deepStrictEqual(hidden('own_variant1', 'fast_variant1', 'fast_variant1_d2', 'fastEntry_variant1'), [false, false, true, false])
  assert.strictEqual(badge('Lib_Mode_Slow_variant1'), undefined)
  // in Driver 2's copy, Fast is described for Driver 2, and Slow, which Driver 2 lacks, is dimmed for it
  assert.deepStrictEqual(hidden('own_variant2', 'fast_variant2', 'fast_variant2_d2', 'fastEntry_variant2'), [false, true, false, true])
  assert.strictEqual(badge('Lib_Mode_Slow_variant2'), 'Not available in Driver 2, .NET Standard 2.0')
  assert.ok(document.getElementById('Lib_Mode_Slow_variant2').classList.contains('applies-to-unavailable'))

  applySelection({}, driver)
  assert.deepStrictEqual(document.querySelector('.api-contents').children.map(e => e.children[0].id), ['own', 'own_variant1', 'own_variant2'])
  assert.deepStrictEqual(hidden('fast', 'fast_d2', 'fastEntry'), [false, true, false])
  assert.strictEqual(document.querySelectorAll('.applies-to-unavailable').length, 0)
})

test('a copy shown whole for units the pick cannot tell apart shows what it holds for all of them', () => {
  // Driver 1 has one copy for .NET Standard 2.0 and .NET Framework 4.6.2: Old exists only on .NET Framework 4.6.2,
  // New only on .NET 8, which the copy is not for
  const type = (suffix: string) => `
    <dl class="parameters">
      <dt id="Lib_Mode_Old${suffix}" data-applies-to="d1:net462">Old</dt><dd>old</dd>
      <dt id="Lib_Mode_New${suffix}" data-applies-to="d1:net8.0 d2:net8.0">New</dt><dd>new</dd>
    </dl>`
  const { document } = installDom(`${site}
  <article>
    <h1 id="Lib_Mode" data-applies-to="d2:net8.0 d1:net8.0 d1:netstandard2.0 d1:net462">Mode</h1>
    <div class="facts"></div>
    <div class="api-contents">
      <div class="api-content" data-applies-to="d2:net8.0">${type('')}</div>
      <div class="api-content" data-applies-to="d1:net8.0" data-search-ignore hidden>${type('_variant1')}</div>
      <div class="api-content" data-applies-to="d1:netstandard2.0 d1:net462" data-search-ignore hidden>${type('_variant2')}</div>
    </div>
    <h5 class="api-variants-heading">In other frameworks</h5>
    <div class="api-variant" id="variant1" data-applies-to="d1:net8.0"><h6 class="api-variant-label">Driver 1</h6><div>summary</div></div>
    <div class="api-variant" id="variant2" data-applies-to="d1:netstandard2.0 d1:net462"><h6 class="api-variant-label">Driver 1</h6><div>summary</div></div>
  </article>`)
  const badge = (id: string) => document.getElementById(id).querySelector('.applies-to-unavailable-badge')?.textContent

  applySelection({ value: 'd1' }, driver)
  assert.strictEqual(document.getElementById('Lib_Mode_Old_variant2').closest('.api-variant').id, 'variant2')
  assert.strictEqual(badge('Lib_Mode_Old_variant2'), undefined)
  assert.strictEqual(badge('Lib_Mode_New_variant2'), 'Not available in Driver 1, .NET Standard 2.0, .NET Framework 4.6.2')
})

test('a value of an enum shown whole shows its declaration with its description', () => {
  // X is 0 for Driver 2, 1 for Driver 1 on .NET 8, and 2 for Driver 1 on .NET Standard 2.0
  const { document } = installDom(`${site}
  <article>
    <h1 id="Lib_Mode" data-applies-to="d2:net8.0 d1:net8.0 d1:netstandard2.0">Mode</h1>
    <div class="facts"></div>
    <dl class="parameters">
      <dt id="Lib_Mode_X" data-applies-to="d2:net8.0 d1:net8.0 d1:netstandard2.0"><span class="api-contents"><span class="api-content" data-applies-to="d2:net8.0"><code id="x">X = 0</code></span><span class="api-content" data-applies-to="d1:net8.0" data-search-ignore hidden><code id="x1">X = 1</code></span><span class="api-content" data-applies-to="d1:netstandard2.0" data-search-ignore hidden><code id="x2">X = 2</code></span></span></dt>
      <dd>
        <div class="api-contents"><div class="api-content" data-applies-to="d2:net8.0"><p id="s">zero</p></div><div class="api-content" data-applies-to="d1:net8.0" data-search-ignore hidden><p id="s1">one</p></div><div class="api-content" data-applies-to="d1:netstandard2.0" data-search-ignore hidden><p id="s2">two</p></div></div>
        <h5 class="api-variants-heading">In other frameworks</h5>
        <div class="api-variant" id="variant1" data-applies-to="d1:net8.0"><h6 class="api-variant-label">Driver 1 (.NET 8)</h6><div id="diff1">X = 1</div></div>
        <div class="api-variant" id="variant2" data-applies-to="d1:netstandard2.0"><h6 class="api-variant-label">Driver 1 (.NET Standard 2.0)</h6><div id="diff2">X = 2</div></div>
      </dd>
    </dl>
  </article>`)
  const hidden = (...ids: string[]) => ids.map(id => !!document.getElementById(id).closest('[hidden]'))
  const entry = (id: string) => document.getElementById(id).closest('.api-variant')?.id

  // Driver 1 on .NET 8 in place, Driver 1 on .NET Standard 2.0 whole in its entry: its declaration, then its description
  applySelection({ value: 'd1' }, driver)
  assert.deepStrictEqual(hidden('x', 'x1', 's1', 'x2', 's2', 'diff2'), [true, false, false, false, false, true])
  assert.deepStrictEqual([entry('x2'), entry('s2')], ['variant2', 'variant2'])
  assert.deepStrictEqual(document.getElementById('variant2').children.map(e => e.id || e.children[0]?.id || e.tagName), ['H6', 'x2', 's2', 'diff2'])

  applySelection({}, driver)
  assert.deepStrictEqual(document.getElementById('Lib_Mode_X').children[0].children.map(e => e.children[0].id), ['x', 'x1', 'x2'])
  assert.deepStrictEqual(hidden('x', 'x1', 'x2', 's', 's2', 'diff2'), [false, true, true, false, true, false])
})

test('a type shown whole shows its see-also links with it', () => {
  // Driver 1 on .NET 10 differs from the API's own content, Driver 1 on .NET 8; Driver 2 has see-also links of its own
  const { document } = installDom(`${site}
  <article>
    <h1 id="Lib_Reader" data-applies-to="d1:net8.0 d2:netstandard2.0 d1:net10.0">Reader</h1>
    <div class="facts"></div>
    <div class="api-contents">
      <div class="api-content" data-applies-to="d1:net8.0"><p id="text">own</p></div>
      <div class="api-content" data-applies-to="d2:netstandard2.0" data-search-ignore hidden><p id="text_variant1">Driver 2</p></div>
      <div class="api-content" data-applies-to="d1:net10.0" data-search-ignore hidden><p id="text_variant2">Driver 1 on .NET 10</p></div>
    </div>
    <h5 class="api-variants-heading">In other frameworks</h5>
    <div class="api-variant" id="variant1" data-applies-to="d2:netstandard2.0"><h6 class="api-variant-label">Driver 2</h6><h6>See also</h6><div id="listed1">Array</div></div>
    <div class="api-variant" id="variant2" data-applies-to="d1:net10.0"><h6 class="api-variant-label">Driver 1</h6><div id="diff2">declaration</div></div>
    <div class="api-contents">
      <div class="api-content" data-applies-to="d1:net8.0"><h2 id="seealso">See Also</h2><div id="span">Span</div></div>
      <div class="api-content" data-applies-to="d2:netstandard2.0" data-search-ignore hidden><h2 id="seealso_variant1">See Also</h2><div id="array">Array</div></div>
      <div class="api-content" data-applies-to="d1:net10.0" data-search-ignore hidden><h2 id="seealso_variant2">See Also</h2><div id="memory">Memory</div></div>
    </div>
  </article>`)
  const hidden = (...ids: string[]) => ids.map(id => !!document.getElementById(id).closest('[hidden]'))
  const outline = () => document.querySelectorAll('article h2').filter(h => !isHiddenBySelection(h as unknown as Element)).map(h => h.id)

  applySelection({ framework: 'net10.0' })
  assert.deepStrictEqual(hidden('span', 'memory', 'text_variant1', 'array', 'listed1'), [true, false, false, false, true])
  assert.deepStrictEqual(document.getElementById('variant1').children.map(e => e.id || e.children[0]?.id || e.tagName), ['H6', 'text_variant1', 'seealso_variant1', 'H6', 'listed1'])
  // the copy's headings stay out of In this article, which has those of the page once
  assert.deepStrictEqual(outline(), ['seealso_variant2'])

  applySelection({})
  assert.deepStrictEqual(document.querySelectorAll('.api-contents')[1].children.map(e => e.children[0].id), ['seealso', 'seealso_variant1', 'seealso_variant2'])
  assert.deepStrictEqual(hidden('span', 'array', 'listed1'), [false, true, false])
  assert.deepStrictEqual(outline(), ['seealso'])
})

test('the id a fragment points to', () => {
  assert.strictEqual(hashId('#Lib_Run%2A'), 'Lib_Run*')
  assert.strictEqual(hashId('#Lib_Run'), 'Lib_Run')
  // not valid percent-encoding: as it is, rather than stopping the picker
  assert.strictEqual(hashId('#%E0%A4'), '%E0%A4')
})

test('the elements of a member', () => {
  const { document } = installDom(page)
  const ids = (id: string) => memberElements(document.getElementById(id) as unknown as Element).map(e => e.tagName + (e.id ? '#' + e.id : ''))

  // a member ends at the anchor of the next overload, which belongs to the next member
  assert.deepStrictEqual(ids('Lib_Shared_Do'), ['H3#Lib_Shared_Do', 'P'])
  assert.deepStrictEqual(ids('Lib_Shared_Size'), ['H3#Lib_Shared_Size', 'DIV', 'H6', 'DIV'])
  assert.deepStrictEqual(ids('Lib_Mode_Strict'), ['DT#Lib_Mode_Strict', 'DD'])
})

test('the see-also of a page that differs between frameworks is not part of the last member', () => {
  const { document } = installDom(`${site}
  <article>
    <h1 id="Lib_Shared" data-applies-to="net8.0 netstandard2.0">Shared</h1>
    <div class="facts"></div>
    <h3 id="Lib_Shared_Do" data-applies-to="net8.0">Do</h3>
    <div class="api-contents">
      <div class="api-content" data-applies-to="net8.0"><div class="summary">Only on .NET 8.</div></div>
    </div>
    <div class="api-contents">
      <div class="api-content" data-applies-to="net8.0"><h2 id="seealso">See Also</h2><div class="seealso"><div id="span">Span</div></div></div>
      <div class="api-content" data-applies-to="netstandard2.0" data-search-ignore hidden><h2 id="seealso_variant1">See Also</h2><div class="seealso"><div id="array">Array</div></div></div>
    </div>
  </article>`)
  const ids = memberElements(document.getElementById('Lib_Shared_Do') as unknown as Element).map(e => e.tagName + (e.id ? '#' + e.id : ''))
  assert.deepStrictEqual(ids, ['H3#Lib_Shared_Do', 'DIV'])

  applySelection({ framework: 'netstandard2.0' })
  assert.ok(document.getElementById('Lib_Shared_Do').classList.contains('applies-to-unavailable'))
  // the see-also for the framework is shown, and not dimmed as the member before it is
  assert.ok(document.getElementById('span').closest('[hidden]'))
  assert.ok(!document.getElementById('array').closest('[hidden]'))
  assert.strictEqual(document.getElementById('array').closest('.applies-to-unavailable'), null)
  assert.strictEqual(document.getElementById('seealso_variant1').closest('.applies-to-unavailable'), null)
})

// A type page as docfx writes it: the example of a member is its markdown as it is, headings included.
const exampleHeadingsPage = `${site}<body><article>
    <h1 id="Lib" data-uid="Lib" data-applies-to="net8.0 net462">Lib</h1><div class="facts"></div>
    <h2 class="section" id="methods">Methods</h2>
    <h3 id="Lib_Stop" data-uid="Lib.Stop" data-applies-to="net8.0">Stop</h3>
    <h4 class="section" id="Lib_Stop_examples">Examples</h4>
    <h2 id="usage">Usage</h2><p>Stop it.</p>
    <h3 id="notes">Notes</h3><p>Only on .NET 8.</p>
    <h3 id="Lib_Run" data-uid="Lib.Run" data-applies-to="net8.0 net462">Run</h3><p>Everywhere.</p>
    <h2 id="seealso">See Also</h2><div class="seealso"><div>Lib</div></div>
  </article></body>`

test('a heading of a member\'s example does not end the member', () => {
  const { document } = installDom(exampleHeadingsPage)
  const ids = (id: string) => memberElements(document.getElementById(id) as unknown as Element).map(e => e.tagName + (e.id ? '#' + e.id : ''))

  assert.deepStrictEqual(ids('Lib_Stop'), ['H3#Lib_Stop', 'H4#Lib_Stop_examples', 'H2#usage', 'P', 'H3#notes', 'P'])
  // nor do the see-also links of the page belong to the last member
  assert.deepStrictEqual(ids('Lib_Run'), ['H3#Lib_Run', 'P'])

  // in print, the member goes whole, and the section keeps the member left
  applySelection({ framework: 'net462' }, undefined, true)
  const hidden = (id: string) => !!document.getElementById(id).closest('[hidden]')
  assert.ok(hidden('Lib_Stop') && hidden('usage') && hidden('notes'))
  assert.ok(!hidden('methods') && !hidden('Lib_Run') && !hidden('seealso'))

  // nor do they when a variant has links of its own, which the page swaps in
  const swapped = installDom(exampleHeadingsPage.replace('<h2 id="seealso">See Also</h2><div class="seealso"><div>Lib</div></div>',
    '<div class="api-contents"><div class="api-content" data-applies-to="net8.0"><h2 id="seealso">See Also</h2></div>' +
    '<div class="api-content" data-applies-to="net462" data-search-ignore hidden><h2 id="seealso_variant1">See Also</h2></div></div>')).document
  assert.deepStrictEqual(memberElements(swapped.getElementById('Lib_Run') as unknown as Element).map(e => e.tagName), ['H3', 'P'])
})

test('in print, the see-also links of variants are not part of the last section', () => {
  const { document } = installDom(`${site}<body><article>
    <h1 id="Lib" data-uid="Lib" data-applies-to="net8.0 net462">Lib</h1><div class="facts"></div>
    <h2 class="section" id="events">Events</h2>
    <h3 id="Lib_Stopped" data-uid="Lib.Stopped" data-applies-to="net8.0">Stopped</h3><p>Only on .NET 8.</p>
    <div class="api-contents" id="links">
      <div class="api-content" data-applies-to="net8.0"><h2 id="seealso">See Also</h2><div class="seealso"><div>Span</div></div></div>
      <div class="api-content" data-applies-to="net462" data-search-ignore hidden><h2 id="seealso_variant1">See Also</h2><div class="seealso"><div>Array</div></div></div>
    </div>
  </article></body>`)
  applySelection({ framework: 'net462' }, undefined, true)
  const hidden = (id: string) => !!document.getElementById(id).closest('[hidden]')
  // the section goes with its only member, the links of the type stay, as the selection has them
  assert.ok(hidden('events') && hidden('Lib_Stopped'))
  assert.ok(!hidden('links') && !hidden('seealso_variant1'))
  assert.strictEqual(document.getElementById('links').closest('[data-applies-to-left-out]'), null)
})

test('a link to content hidden for the selection goes to the content shown', () => {
  installDom(page)
  applySelection({ framework: 'netstandard2.0' })
  assert.strictEqual(shownTarget('Lib_Shared_Size_remarks').id, 'Lib_Shared_Size_variant1_remarks')
  assert.strictEqual(shownTarget('Lib_Shared_Do').id, 'Lib_Shared_Do')

  applySelection({})
  assert.strictEqual(shownTarget('Lib_Shared_Size_variant1_remarks').id, 'Lib_Shared_Size_remarks')
  assert.strictEqual(shownTarget('Lib_Shared_Size_remarks').id, 'Lib_Shared_Size_remarks')
  assert.strictEqual(shownTarget('missing'), null)
})

test('a link to an API named like the suffix of a copy goes to the same element of the content shown', () => {
  installDom(`${site}
  <article>
    <div class="api-contents">
      <div class="api-content" data-applies-to="net8.0"><h2 id="Lib_Get_variant2_remarks">Remarks</h2></div>
      <div class="api-content" data-applies-to="netstandard2.0" hidden><h2 id="Lib_Get_variant2_variant1_remarks">Remarks</h2></div>
      <div class="api-content" data-applies-to="net462" hidden><h2 id="Lib_Get_variant2_variant2_remarks">Remarks</h2></div>
    </div>
  </article>`)

  applySelection({ framework: 'netstandard2.0' })
  assert.strictEqual(shownTarget('Lib_Get_variant2_remarks').id, 'Lib_Get_variant2_variant1_remarks')
  applySelection({ framework: 'net462' })
  assert.strictEqual(shownTarget('Lib_Get_variant2_variant1_remarks').id, 'Lib_Get_variant2_variant2_remarks')
  applySelection({ framework: 'net8.0' })
  assert.strictEqual(shownTarget('Lib_Get_variant2_variant2_remarks').id, 'Lib_Get_variant2_remarks')
})

test('a link on the page to content hidden for the selection scrolls to the content shown', () => {
  installDom(page, 'https://example.org/docs/api/Lib.Shared.html#Lib_Shared_Size_remarks')
  const scrolled = (id: string) => !!(globalThis.document.getElementById(id) as unknown as { scrolledIntoView?: boolean }).scrolledIntoView
  applySelection({ framework: 'netstandard2.0' })

  scrollToShownTarget(true)
  assert.ok(scrolled('Lib_Shared_Size_variant1_remarks'))
  assert.ok(!scrolled('Lib_Shared_Size_remarks'))

  // the browser scrolls to what is not hidden itself
  installDom(page, 'https://example.org/docs/api/Lib.Shared.html#Lib_Shared_Do')
  scrollToShownTarget(true)
  assert.ok(!scrolled('Lib_Shared_Do'))
})

test('the lists of a page dim what does not exist in the selection', () => {
  const { document } = installDom(`${site}
  <article>
    <h1 id="Lib" data-applies-to="net8.0 netstandard2.0">Lib</h1>
    <h3 id="classes">Classes</h3>
    <dl class="jumplist" id="Shared" data-applies-to="net8.0 netstandard2.0"><dt>Shared</dt><dd></dd></dl>
    <dl class="jumplist" id="OnlyNet8" data-applies-to="net8.0"><dt>OnlyNet8</dt><dd></dd></dl>
    <dl class="typelist implements"><dt>Implements</dt><dd><div id="IAlways">IAlways</div><div id="ISource" data-applies-to="net8.0">ISource</div></dd></dl>
    <dl class="typelist derived"><dt>Derived</dt><dd><div id="Child">Child</div><div id="Switches" data-applies-to="net8.0">Switches</div></dd></dl>
    <dl class="typelist inheritedMembers"><dt>Inherited Members</dt><dd><div id="Run" data-applies-to="net8.0">Base.Run()</div></dd></dl>
    <dl class="typelist extensionMethods"><dt>Extension Methods</dt><dd><div id="Twice" data-applies-to="net8.0">Ext.Twice()</div></dd></dl>
  </article>`)
  const dimmed = () => document.querySelectorAll('.applies-to-unavailable').map(e => e.id)
  // marked in words too, not by the colour only
  const marked = () => document.querySelectorAll('.applies-to-unavailable-badge').map(b => (b.closest('dl.jumplist') || b.parentElement).id + ': ' + b.textContent)

  applySelection({ framework: 'netstandard2.0' })
  assert.deepStrictEqual(dimmed(), ['OnlyNet8', 'ISource', 'Switches', 'Run', 'Twice'])
  assert.deepStrictEqual(marked(), ['OnlyNet8', 'ISource', 'Switches', 'Run', 'Twice'].map(id => `${id}: Not available in .NET Standard 2.0`))
  applySelection({ framework: 'net10.0' })
  assert.deepStrictEqual(dimmed(), [])
  assert.deepStrictEqual(marked(), [])
  applySelection({ framework: 'netstandard2.0' })
  applySelection({})
  assert.deepStrictEqual(dimmed(), [])
  assert.deepStrictEqual(marked(), [])
})

test('an extension method of another part of the docs is dimmed by its framework only', () => {
  const { document } = installDom(`${site}
  <article>
    <h1 id="Lib_Context" data-applies-to="ef3:netstandard2.0 ef9:net8.0">Context</h1>
    <dl class="typelist extensionMethods"><dt>Extension Methods</dt><dd>
      <div id="Driver" data-applies-to="d1:net8.0">DriverExt.Use()</div>
      <div id="Ef9" data-applies-to="ef9:net8.0">EfExt.Track()</div>
    </dd></dl>
  </article>`)
  const dimmed = () => document.querySelectorAll('.applies-to-unavailable').map(e => e.id)

  // the value of the Driver axis says nothing about EF Core 3.1
  applySelection({ value: 'ef3' }, ef)
  assert.deepStrictEqual(dimmed(), ['Ef9'])
  applySelection({ value: 'ef9', framework: 'netstandard2.0' }, ef)
  assert.deepStrictEqual(dimmed(), ['Driver', 'Ef9'])
})

test('a namespace that does not exist in the selection says so', () => {
  const { document } = installDom(`${site}
  <article>
    <h1 id="Lib_Data" data-applies-to="net8.0">Lib.Data</h1>
    <div class="markdown summary"></div>
    <h3 id="classes">Classes</h3>
    <dl class="jumplist" data-applies-to="net8.0"><dt>Cache</dt><dd></dd></dl>
  </article>`)

  applySelection({ framework: 'netstandard2.0' })
  const banner = document.getElementById('Lib_Data').nextElementSibling
  assert.strictEqual(banner.className, 'applies-to-banner alert alert-warning')
  assert.strictEqual(banner.textContent, 'Not available in .NET Standard 2.0')
  applySelection({})
  assert.strictEqual(document.querySelector('.applies-to-banner'), null)
})

test('In this article leaves out only what the selection hides', () => {
  const { document } = installDom(`${site}
  <article>
    <div class="tabGroup"><section id="tab-a"><h2 id="setup-a">Setup</h2></section><section id="tab-b" hidden="hidden"><h2 id="setup-b">Setup</h2></section></div>
    <div class="api-contents">
      <div class="api-content" data-applies-to="net8.0"><h2 id="example">Example</h2></div>
      <div class="api-content" data-applies-to="netstandard2.0" data-search-ignore hidden><h2 id="example_variant1">Example</h2></div>
    </div>
    <h5 class="api-variants-heading" hidden>In other frameworks</h5>
    <div class="api-variant" data-applies-to="netstandard2.0" hidden><h3 id="in-variant">Remarks</h3></div>
  </article>`)

  // a tab not picked is listed, as it is without the picker
  assert.deepStrictEqual(document.querySelectorAll('article h2, article h3').filter(h => !isHiddenBySelection(h as unknown as Element)).map(h => h.id), ['setup-a', 'setup-b', 'example'])
})

test('Applies to names what the selection shows', () => {
  const markup = (appliesTo: string, line: string, frameworksHidden = false) => `${site}
  <article>
    <h1 id="Lib_Reader" data-applies-to="${appliesTo}">Reader</h1>
    <div class="facts"><dl class="applies-to-line"${frameworksHidden ? ' data-frameworks-hidden' : ''}><dt>Applies to</dt><dd>${line}</dd></dl></div>
  </article>`
  const shown = (document: { querySelectorAll: (s: string) => { hidden: boolean, textContent: string }[] }) =>
    document.querySelectorAll('.applies-to-line dd').filter(dd => !dd.hidden).map(dd => dd.textContent)

  const { document } = installDom(markup('net8.0 netstandard2.0 net462', '.NET 8, .NET Standard 2.0, .NET Framework 4.6.2'))
  applySelection({ framework: 'net462' })
  assert.deepStrictEqual(shown(document), ['.NET Framework 4.6.2'])
  // .NET 10 has the .NET 8 build
  applySelection({ framework: 'net10.0' })
  assert.deepStrictEqual(shown(document), ['.NET 8'])
  applySelection({})
  assert.deepStrictEqual(shown(document), ['.NET 8, .NET Standard 2.0, .NET Framework 4.6.2'])
  // a framework the API does not exist in has a banner, and the line as it is
  applySelection({ framework: 'netcoreapp1.0' })
  assert.deepStrictEqual(shown(document), ['.NET 8, .NET Standard 2.0, .NET Framework 4.6.2'])

  const withAxis = installDom(markup('ef3:netstandard2.0 ef3:net462 ef9:net8.0', '<strong>EF Core 3.1</strong> (.NET Standard 2.0, .NET Framework 4.6.2), <strong>EF Core 9</strong> (.NET 8)')).document
  applySelection({ value: 'ef3' }, ef)
  assert.deepStrictEqual(shown(withAxis), ['EF Core 3.1 (.NET Standard 2.0, .NET Framework 4.6.2)'])
  // a framework: the build of each value that stands in best
  applySelection({ framework: 'net10.0' }, ef)
  assert.deepStrictEqual(shown(withAxis), ['EF Core 3.1 (.NET Standard 2.0), EF Core 9 (.NET 8)'])
  applySelection({ value: 'ef3', framework: 'net472' }, ef)
  assert.deepStrictEqual(shown(withAxis), ['EF Core 3.1 (.NET Framework 4.6.2)'])
  // before the table of contents names the axis: the line as the page has it, not the ids of its values
  applySelection({ framework: 'net10.0' })
  assert.deepStrictEqual(shown(withAxis), ['EF Core 3.1 (.NET Standard 2.0, .NET Framework 4.6.2), EF Core 9 (.NET 8)'])

  // without the frameworks of each value, as `_disableApiAppliesToFrameworks` has it, whatever the labels are
  const lts: Axis = { name: 'EF Core', values: [{ id: 'ef3', label: 'EF Core 3.1 (LTS)' }, { id: 'ef9', label: 'EF Core 9' }] }
  const valuesOnly = installDom(markup('ef3:net462 ef9:net8.0', '<strong>EF Core 3.1 (LTS)</strong>, <strong>EF Core 9</strong>', true)).document
  applySelection({ framework: 'net8.0' }, lts)
  assert.deepStrictEqual(shown(valuesOnly), ['EF Core 9'])
  applySelection({ framework: 'net462' }, lts)
  assert.deepStrictEqual(shown(valuesOnly), ['EF Core 3.1 (LTS)'])
})

test('the copies of an API have ids of their own', () => {
  const { document } = installDom(`${site}
  <article>
    <div class="api-contents">
      <div class="api-content" data-applies-to="net8.0"><h4 id="parameters-1">Parameters</h4><h2 id="example">Example</h2><h4 id="parameters-2">Parameters</h4></div>
      <div class="api-content" data-applies-to="netstandard2.0" data-search-ignore hidden><h4>Parameters</h4><h2 id="example">Example</h2><h4>Parameters</h4><h4>Returns</h4></div>
      <div class="api-content" data-applies-to="net462" data-search-ignore hidden><h2 id="example">Example</h2><h4 id="Lib_Run_variant2_x">X</h4></div>
    </div>
  </article>`)
  distinguishCopies()
  const ids = (selector: string) => document.querySelectorAll(selector).map(e => e.id)

  // a heading of the summary or remarks the copy repeats, and a heading named like the same of the API's own content
  assert.deepStrictEqual(ids('[data-applies-to="netstandard2.0"] h2, [data-applies-to="netstandard2.0"] h4'), ['parameters-1_variant1', 'example_variant1', 'parameters-2_variant1', ''])
  assert.deepStrictEqual(ids('[data-applies-to="net462"] h2, [data-applies-to="net462"] h4'), ['example_variant2', 'Lib_Run_variant2_x'])
  assert.deepStrictEqual(ids('[data-applies-to="net8.0"] h2, [data-applies-to="net8.0"] h4'), ['parameters-1', 'example', 'parameters-2'])

  // which a link to the API's own content finds in the copy shown
  applySelection({ framework: 'netstandard2.0' })
  assert.strictEqual(shownTarget('parameters-2').id, 'parameters-2_variant1')
  assert.strictEqual(shownTarget('example').id, 'example_variant1')
})

test('the tabs of a copy control the panels of the copy', () => {
  const tabs = '<div class="tabGroup"><ul role="tablist"><li><a href="#tabpanel_x_a" role="tab" aria-controls="tabpanel_x_a">A</a></li></ul><section id="tabpanel_x_a" role="tabpanel">A</section></div>'
  const { document } = installDom(`${site}
  <article>
    <div class="api-contents">
      <div class="api-content" data-applies-to="net8.0">${tabs}</div>
      <div class="api-content" data-applies-to="netstandard2.0" data-search-ignore hidden>${tabs}</div>
    </div>
  </article>`)
  distinguishCopies()
  const tab = (framework: string) => document.querySelector(`[data-applies-to="${framework}"] a`)

  assert.strictEqual(tab('netstandard2.0').getAttribute('aria-controls'), 'tabpanel_x_a_variant1')
  assert.strictEqual(tab('netstandard2.0').getAttribute('href'), '#tabpanel_x_a_variant1')
  assert.ok(document.getElementById('tabpanel_x_a_variant1'))
  assert.strictEqual(tab('net8.0').getAttribute('aria-controls'), 'tabpanel_x_a')
  assert.strictEqual(tab('net8.0').getAttribute('href'), '#tabpanel_x_a')
})

test('the headings of the page are named as without copies, those of the copies after them', () => {
  const { document } = installDom(`${site}
  <article>
    <h2>Methods</h2>
    <div class="api-contents">
      <div class="api-content" data-applies-to="net8.0"><h4>Parameters</h4></div>
      <div class="api-content" data-applies-to="netstandard2.0" data-search-ignore hidden><h4>Parameters</h4><h4>Returns</h4></div>
    </div>
    <h4>Parameters</h4>
    <h4 class="no-anchor">Remarks</h4>
  </article>`)
  // as AnchorJS names a heading without an id: after its text, with a number after a name the page has
  const calls: string[][] = []
  const add = (headings: Element[]) => {
    calls.push(headings.map(h => h.textContent))
    for (const heading of headings.filter(h => !h.id)) {
      const name = heading.textContent.toLowerCase()
      let id = name
      for (let n = 1; document.getElementById(id); n++) {
        id = `${name}-${n}`
      }
      heading.id = id
    }
  }
  addHeadingAnchors(add)

  // the page's own first, the copies' after distinguishCopies named them after those
  assert.deepStrictEqual(calls, [['Methods', 'Parameters', 'Parameters'], ['Parameters', 'Returns']])
  const ids = (selector: string) => document.querySelectorAll(selector).map(e => e.id)
  // in the order of the page: the API's own content, its copy, the heading after them, one without an anchor
  assert.deepStrictEqual(ids('article h2, article h4'), ['methods', 'parameters', 'parameters_variant1', 'returns', 'parameters-1', ''])
})

test('a dropdown keeps what the reader picked', () => {
  const { storage, window } = installDom(site, 'https://example.org/docs/api/A.html?framework=net8.0&x=1')
  let changes = 0
  window.addEventListener('docfx:appliestochange', () => changes++)

  select('framework', 'netstandard2.0')
  assert.strictEqual(storage.get('docfx.axis./docs/.framework'), 'netstandard2.0')
  // the query string would override it on this page
  assert.strictEqual(globalThis.window.location.search, '?x=1')
  assert.strictEqual(changes, 1)

  select('ef-core', 'ef3')
  assert.strictEqual(storage.get('docfx.axis./docs/.ef-core'), 'ef3')
  // "All"
  select('framework', undefined)
  assert.ok(!storage.has('docfx.axis./docs/.framework'))
  assert.strictEqual(changes, 3)
})

test('a dropdown keeps what the reader picked for the page where local storage is null', () => {
  installDom(site, 'https://example.org/docs/api/A.html?framework=net8.0')
  // as in a browser set to keep no site data
  Object.defineProperty(globalThis, 'localStorage', { configurable: true, writable: true, value: null })

  assert.strictEqual(getSelection(ef, frameworks).framework, 'net8.0')
  select('framework', 'netstandard2.0')
  // the address no longer names .NET 8, and the pick is not lost
  assert.strictEqual(globalThis.window.location.search, '')
  assert.deepStrictEqual(getSelection(ef, frameworks), { framework: 'netstandard2.0', value: undefined })
  select('ef-core', 'ef3')
  assert.deepStrictEqual(getSelection(ef, frameworks), { framework: 'netstandard2.0', value: 'ef3' })
  // "All"
  select('framework', undefined)
  select('ef-core', undefined)
  assert.deepStrictEqual(getSelection(ef, frameworks), { framework: undefined, value: undefined })
})

test('a dropdown keeps what the reader picked for the page where local storage refuses it', () => {
  const { window } = installDom(site, 'https://example.org/docs/api/A.html')
  let changes = 0
  window.addEventListener('docfx:appliestochange', () => changes++)
  const storage = globalThis.localStorage
  const setItem = storage.setItem
  // as when it is full
  storage.setItem = () => { throw new Error('QuotaExceededError') }

  select('framework', 'netstandard2.0')
  assert.strictEqual(changes, 1)
  assert.strictEqual(getSelection(ef, frameworks).framework, 'netstandard2.0')
  storage.setItem = setItem
  select('framework', 'net8.0')
  assert.strictEqual(getSelection(ef, frameworks).framework, 'net8.0')
  select('framework', undefined)
  assert.strictEqual(getSelection(ef, frameworks).framework, undefined)
})

test('the navbar leaves out what the selection lacks where its table of contents is that of an API', () => {
  const { storage } = installDom(`${site}<meta name="docfx:appliestopicker" content="true">`, 'https://example.org/docs/api/A.html')
  const navToc: Toc = {
    items: [
      { name: 'Lib', href: 'Lib.html', type: 'Namespace', appliesTo: [{ frameworks: ['net8.0', 'netstandard2.0'] }] },
      { name: 'Lib.Modern', href: 'Lib.Modern.html', type: 'Namespace', appliesTo: [{ frameworks: ['net8.0'] }] },
      { name: 'Lib.Data', type: 'Namespace', appliesTo: [{ frameworks: ['net8.0', 'netstandard2.0'] }], items: [{ name: 'Span', href: 'Span.html', appliesTo: [{ frameworks: ['net8.0'] }] }] }
    ]
  }
  const [lib, modern, data] = navToc.items
  const shown = () => [lib, modern, data, data.items[0]].map(e => isNavEntryShown(navToc, e, e === data.items[0] ? data : undefined, false))

  assert.deepStrictEqual(shown(), [true, true, true, true])
  storage.set('docfx.axis./docs/.framework', 'netstandard2.0')
  // a dropdown whose entries all go goes too
  assert.deepStrictEqual(shown(), [true, false, false, false])
  // the page being read stays
  assert.strictEqual(isNavEntryShown(navToc, modern, undefined, true), true)
  // a navbar of articles names no framework
  const articles: Toc = { items: [{ name: 'Docs', href: 'docs.html' }] }
  assert.strictEqual(isNavEntryShown(articles, articles.items[0], undefined, false), true)
})

test('Next article reads a table of contents as before without the picker', () => {
  // `appliesTo` and `axis` of other shapes, which the picker cannot read
  const toc = {
    items: [
      { name: 'Vertical', href: 'vertical.html', axis: 'vertical', appliesTo: 'Windows only' },
      { name: 'Group', axis: { name: 1 }, items: [{ name: 'Inner', href: 'inner.html', appliesTo: [{ frameworks: 'net8.0' }] }] }
    ]
  } as unknown as Toc
  installDom(site, 'https://example.org/docs/api/A.html')
  assert.deepStrictEqual(articleEntries(toc, []).map(e => e.name), ['Vertical', 'Inner'])

  // with the picker, the entries the table of contents shows for the selection
  const { storage } = installDom(`${site}<meta name="docfx:appliestopicker" content="true">`, 'https://example.org/docs/api/A.html')
  storage.set('docfx.axis./docs/.framework', 'netstandard2.0')
  const apis: Toc = {
    items: [
      { name: 'Shared', href: 'Shared.html', appliesTo: [{ frameworks: ['net8.0', 'netstandard2.0'] }] },
      { name: 'OnlyNet8', href: 'OnlyNet8.html', appliesTo: [{ frameworks: ['net8.0'] }] },
      { name: 'Guide', href: 'guide.html' }
    ]
  }
  assert.deepStrictEqual(articleEntries(apis, []).map(e => e.name), ['Shared', 'Guide'])
  // the page being read stays
  assert.deepStrictEqual(articleEntries(apis, [apis.items[1]]).map(e => e.name), ['Shared', 'OnlyNet8', 'Guide'])
})

const pickerSite = `${site}
  <meta name="docfx:appliestopicker" content="true">
  <meta name="docfx:tocrel" content="toc.html">`

function fetchTocs(tocs: Record<string, Toc>) {
  Object.defineProperty(globalThis, 'fetch', {
    configurable: true,
    writable: true,
    value: async(url: URL) => ({ json: async() => tocs[url.pathname] })
  })
}

test('the page shows what was picked on load, and again when it changes elsewhere', async() => {
  fetchTocs({ '/docs/api/toc.json': { items: [{ name: 'Shared', href: 'Lib.Shared.html', appliesTo: [{ frameworks: ['net8.0', 'netstandard2.0'] }] }] } })
  const { document, storage, window } = installDom(pickerSite + page)
  const shown = (id: string) => !document.getElementById(id).closest('[hidden]')
  storage.set('docfx.axis./docs/.framework', 'netstandard2.0')
  let applied = 0

  await initAppliesTo(() => applied++)
  assert.ok(shown('Lib_Shared_Size_variant1_remarks'))
  assert.ok(!shown('Lib_Shared_Size_remarks'))
  // before the table of contents is loaded, and after
  assert.strictEqual(applied, 2)

  // picked in another tab
  storage.set('docfx.axis./docs/.framework', 'net8.0')
  window.dispatchEvent({ type: 'storage', key: 'docfx.axis./docs/.framework' } as unknown as Event)
  assert.ok(shown('Lib_Shared_Size_remarks'))
  window.dispatchEvent({ type: 'storage', key: 'tocFilter' } as unknown as Event)
  assert.strictEqual(applied, 3)

  // back to a page the browser kept, after picking another framework on the next page
  storage.set('docfx.axis./docs/.framework', 'netstandard2.0')
  window.dispatchEvent({ type: 'pageshow', persisted: false } as unknown as Event)
  assert.ok(shown('Lib_Shared_Size_remarks'))
  window.dispatchEvent({ type: 'pageshow', persisted: true } as unknown as Event)
  assert.ok(shown('Lib_Shared_Size_variant1_remarks'))
})

test('a pick in another tab is kept where the table of contents does not list it', async() => {
  fetchTocs({ '/docs/api/toc.json': { items: [{ name: 'Shared', href: 'Lib.Shared.html', appliesTo: [{ frameworks: ['net8.0', 'netstandard2.0'] }] }] } })
  const { document, storage, window } = installDom(pickerSite + page)
  await initAppliesTo()

  // another tab, in a part of the site that lists .NET Framework 4.6.2, picks it: this one shows all frameworks,
  // and leaves the pick as it is, which the other tab would otherwise undo
  storage.set('docfx.axis./docs/.framework', 'net462')
  window.dispatchEvent({ type: 'storage', key: 'docfx.axis./docs/.framework' } as unknown as Event)
  assert.strictEqual(storage.get('docfx.axis./docs/.framework'), 'net462')
  assert.ok(!document.getElementById('Lib_Shared_Size_remarks').closest('[hidden]'))
  window.dispatchEvent({ type: 'pageshow', persisted: true } as unknown as Event)
  assert.strictEqual(storage.get('docfx.axis./docs/.framework'), 'net462')
})

test('a framework the page does not name is not applied before the table of contents is loaded', async() => {
  fetchTocs({ '/docs/api/toc.json': { items: [{ name: 'Shared', href: 'Lib.Shared.html', appliesTo: [{ frameworks: ['net8.0', 'netstandard2.0'] }] }] } })
  const { storage } = installDom(pickerSite + page)
  storage.set('docfx.axis./docs/.framework', 'net462')
  let applied = 0

  await initAppliesTo(() => applied++)
  // only once the table of contents is loaded, which leaves it out, and keeps it
  assert.strictEqual(applied, 1)
  assert.strictEqual(storage.get('docfx.axis./docs/.framework'), 'net462')
})

test('an extension method of another part does not name a framework for the page before the table of contents is loaded', async() => {
  fetchTocs({ '/docs/api/toc.json': { items: [{ name: 'Shared', href: 'Lib.Shared.html', appliesTo: [{ frameworks: ['net8.0', 'netstandard2.0'] }] }] } })
  const { storage } = installDom(`${pickerSite}
  <article>
    <h1 id="Lib_Shared" data-applies-to="net8.0 netstandard2.0">Shared</h1>
    <dl class="typelist extensionMethods"><dt>Extension Methods</dt><dd><div id="Old" data-applies-to="net462">OldExt.Use()</div></dd></dl>
  </article>`)
  storage.set('docfx.axis./docs/.framework', 'net462')
  let applied = 0

  await initAppliesTo(() => applied++)
  // only once the table of contents is loaded, which does not list it
  assert.strictEqual(applied, 1)
})

test('the page leaves out what the build of its part for the framework picked lacks', async() => {
  fetchTocs({
    '/docs/api/toc.json': {
      items: [{ name: 'Shared', href: 'Lib.Shared.html', type: 'Class', appliesTo: [{ frameworks: ['net8.0', 'netstandard2.0'] }] }]
    }
  })
  const { document, storage } = installDom(`${pickerSite}
  <article>
    <h1 id="Lib_Shared" data-applies-to="net8.0 netstandard2.0">Shared</h1>
    <div class="facts"></div>
    <h3 id="Lib_Shared_Legacy" data-applies-to="netstandard2.0">Legacy</h3>
    <p>Only on .NET Standard 2.0.</p>
    <dl class="typelist extensionMethods"><dt>Extension Methods</dt><dd><div id="Ext" data-applies-to="netstandard2.0">Ext.Use()</div></dd></dl>
  </article>`)
  const dimmed = () => document.querySelectorAll('.applies-to-unavailable').map(e => e.id).filter(id => id)
  storage.set('docfx.axis./docs/.framework', 'net8.0')
  const seen: string[][] = []

  await initAppliesTo(() => seen.push(dimmed()))
  // before the table of contents is loaded too, by what the page's own APIs are documented for; an extension method,
  // which another part, built for .NET Standard 2.0 only, can declare, is not
  assert.deepStrictEqual(seen, [['Lib_Shared_Legacy'], ['Lib_Shared_Legacy']])
  select('framework', 'netstandard2.0')
  assert.deepStrictEqual(dimmed(), [])
})

test('a page whose table of contents lists no frameworks shows them all', async() => {
  fetchTocs({ '/docs/other/toc.json': { items: [{ name: 'Guide', href: 'guide.html' }] } })
  const { document, storage } = installDom(pickerSite + page, 'https://example.org/docs/other/guide.html')
  storage.set('docfx.axis./docs/.framework', 'netstandard2.0')
  let applied = 0

  await initAppliesTo(() => applied++)
  // applied before the table of contents is loaded, then undone
  assert.strictEqual(applied, 2)
  assert.ok(!document.getElementById('Lib_Shared_Size_remarks').closest('[hidden]'))
  assert.ok(document.querySelectorAll('.api-variant').every(e => !e.hidden))
})

test('the dropdowns are named with what they pick', () => {
  const locs = `${pickerSite}
  <meta name="loc:appliesToFramework" content="Framework">
  <meta name="loc:appliesToAll" content="All">
  <meta name="loc:appliesToAllFrameworks" content="All frameworks">
  <meta name="loc:appliesToPickerLabel" content="{name}: {value}">`
  const isTemplate = (v: unknown): v is TemplateResult => Array.isArray((v as TemplateResult)?.strings)
  // the templates of the dropdowns, and the value bound to the name of each button
  const templates = (result: TemplateResult): TemplateResult[] => [result, ...result.values.filter(isTemplate).flatMap(templates)]
  const names = (result: TemplateResult) => templates(result).flatMap(r => r.strings.flatMap((s, i) => s.endsWith("aria-label='") ? [r.values[i]] : []))

  installDom(locs, 'https://example.org/docs/api/A.html?framework=net8.0')
  assert.deepStrictEqual(names(appliesToPicker({ frameworks, axis: ef, key: 'ef-core' })), ['EF Core: All', 'Framework: .NET 8'])
  installDom(locs, 'https://example.org/docs/api/A.html?ef-core=ef3')
  assert.deepStrictEqual(names(appliesToPicker({ frameworks, axis: ef, key: 'ef-core' })), ['EF Core: EF Core 3.1', 'Framework: All frameworks'])
})

// An API of an axis whose own content is for EF Core 8; EF Core 3.1 builds for .NET Standard 2.0, and differently for
// .NET Framework 4.6.2.
const efPage = `${site}
  <article>
    <h1 id="Lib_Query" data-applies-to="ef8:net8.0 ef3:netstandard2.0 ef3:net462">Query</h1>
    <div class="facts"></div>
    <div class="api-contents">
      <div class="api-content" data-applies-to="ef8:net8.0"><p id="own">EF Core 8</p></div>
      <div class="api-content" data-applies-to="ef3:netstandard2.0" data-search-ignore hidden><p id="ef3">EF Core 3.1</p></div>
      <div class="api-content" data-applies-to="ef3:net462" data-search-ignore hidden><p id="ef3net462">EF Core 3.1 on .NET Framework</p></div>
    </div>
    <h5 class="api-variants-heading">In other frameworks</h5>
    <div class="api-variant" id="variant1" data-applies-to="ef3:netstandard2.0"></div>
    <div class="api-variant" id="variant2" data-applies-to="ef3:net462"></div>
  </article>`

test('a value picked alone keeps what differs in the other frameworks of that value', () => {
  const { document } = installDom(efPage)
  const hidden = (id: string) => !!document.getElementById(id).closest('[hidden]')

  applySelection({ value: 'ef3' })
  // the content of EF Core 3.1 is shown in place, and its .NET Framework variant listed under it
  assert.ok(hidden('own'))
  assert.ok(!hidden('ef3'))
  assert.ok(hidden('variant1'))
  assert.ok(!hidden('variant2'))
  assert.ok(!document.querySelector('.api-variants-heading').hidden)

  // EF Core 8 has nothing in other frameworks
  applySelection({ value: 'ef8' })
  assert.ok(!hidden('own'))
  assert.ok(hidden('variant1') && hidden('variant2'))
  assert.ok(document.querySelector('.api-variants-heading').hidden)

  // a framework picked shows its content only
  applySelection({ value: 'ef3', framework: 'net462' })
  assert.ok(!hidden('ef3net462'))
  assert.ok(hidden('variant1') && hidden('variant2'))
})

test('a value of another axis says nothing about the page', () => {
  installDom(efPage)
  // a PDF of the driver `d1` over the API of two axes is told the values of the driver axis
  const driver = { value: 'd1', axisValues: ['d1', 'd2'] }
  assert.ok(isAvailable(parseUnits('ef8:net8.0'), driver))
  assert.ok(!isAvailable(parseUnits('d2:net8.0'), driver))
  assert.ok(isAvailable(parseUnits('d1:net8.0'), driver))
})

const printPage = `${site}
  <body>
    <article>
      <h1 id="Lib" data-applies-to="net8.0 net462">Lib</h1>
      <div class="facts"></div>
      <dl class="typelist implements"><dt>Implements</dt><dd><div id="Implements_All">IAll</div><div id="Implements_Net8" data-applies-to="net8.0">INet8</div></dd></dl>
      <dl class="typelist derived"><dt>Derived</dt><dd><div id="Derived_All">All</div><div id="Derived_Net8" data-applies-to="net8.0">Net8</div></dd></dl>
      <dl class="typelist inheritedMembers" id="inherited"><dt>Inherited Members</dt><dd><div id="Inherited_Net8" data-applies-to="net8.0">Net8</div></dd></dl>
      <dl class="typelist extensionMethods" id="extensions"><dt>Extension Methods</dt><dd><div id="Extension_Net8" data-applies-to="net8.0">Ext.Twice()</div></dd></dl>
      <h2 id="events">Events</h2>
      <a id="Lib_Changed_" data-overload></a>
      <h3 id="Lib_Changed" data-applies-to="net8.0">Changed</h3>
      <p>Only on .NET 8.</p>
      <h2 id="methods">Methods</h2>
      <h3 id="Lib_Run" data-applies-to="net8.0 net462">Run</h3>
      <p>Everywhere.</p>
      <h3 id="Lib_Stop" data-applies-to="net8.0">Stop</h3>
      <p>Only on .NET 8.</p>
      <h3 id="classes">Classes</h3>
      <dl class="jumplist" data-applies-to="net8.0 net462"><dt>Everywhere</dt></dl>
      <h3 id="interfaces">Interfaces</h3>
      <dl class="jumplist" data-applies-to="net8.0"><dt>IOnlyNet8</dt></dl>
    </article>
  </body>`

test('in print, what does not exist in the selection is left out', () => {
  const { document } = installDom(printPage)
  const hidden = (id: string) => !!document.getElementById(id).closest('[hidden]')

  applySelection({ framework: 'net462' }, undefined, true)
  // members, and a section with none left
  assert.ok(hidden('Lib_Changed') && hidden('events'))
  assert.ok(!hidden('Lib_Run') && !hidden('methods'))
  assert.ok(hidden('Lib_Stop'))
  // the types of a namespace page, and a group of them with none left
  assert.ok(!hidden('classes'))
  assert.ok(hidden('interfaces'))
  assert.ok(document.querySelectorAll('dl.jumplist')[1].hidden)
  // the interfaces implemented, the classes derived, the members inherited and the extension methods, in other
  // frameworks only, and a list with none left
  assert.ok(!hidden('Implements_All') && hidden('Implements_Net8'))
  assert.ok(!hidden('Derived_All') && hidden('Derived_Net8'))
  assert.ok(hidden('Inherited_Net8') && hidden('inherited'))
  assert.ok(hidden('Extension_Net8') && hidden('extensions'))
  // what is left out is marked for `docfx pdf`, which makes the links to it text
  assert.deepStrictEqual(
    document.querySelectorAll('[data-applies-to-left-out][id], [data-applies-to-left-out] [id]').map(e => e.id),
    ['Implements_Net8', 'Derived_Net8', 'inherited', 'Inherited_Net8', 'extensions', 'Extension_Net8', 'events', 'Lib_Changed_', 'Lib_Changed', 'Lib_Stop', 'interfaces'])
  // nothing is marked, as nothing is left to mark
  assert.deepStrictEqual(document.querySelectorAll('.applies-to-unavailable-badge, .applies-to-banner'), [])
  assert.ok(!document.body.hasAttribute('data-applies-to-unavailable'))

  // the page of an API that does not exist in the selection says so to `docfx pdf`
  applySelection({ framework: 'netcoreapp1.0' }, undefined, true)
  assert.ok(document.body.hasAttribute('data-applies-to-unavailable'))
  assert.strictEqual(document.querySelector('.applies-to-banner'), null)
})

test('in print, an overload group goes with the last of its members', () => {
  const { document } = installDom(`${site}<body><article>
    <h1 id="Lib" data-applies-to="net8.0 net462">Lib</h1><div class="facts"></div>
    <h2 id="methods">Methods</h2>
    <h3 id="Lib_Run" data-applies-to="net8.0 net462">Run</h3><p>Everywhere.</p>
    <a id="Lib_Retry_" data-overload></a><h3 id="Lib_Retry" data-applies-to="net8.0">Retry()</h3><p>Only on .NET 8.</p>
    <a id="Lib_Retry_" data-overload></a><h3 id="Lib_Retry_int" data-applies-to="net8.0">Retry(int)</h3><p>Only on .NET 8.</p>
    <a id="Lib_Do_" data-overload></a><h3 id="Lib_Do" data-applies-to="net8.0">Do()</h3><p>Only on .NET 8.</p>
    <a id="Lib_Do_" data-overload></a><h3 id="Lib_Do_int" data-applies-to="net8.0 net462">Do(int)</h3><p>Everywhere.</p>
  </article></body>`)

  applySelection({ framework: 'net462' }, undefined, true)
  // which a link to the group is text for, as for a member left out
  const anchors = document.querySelectorAll('a[data-overload]')
  assert.deepStrictEqual(anchors.map(a => `${a.id} ${a.hasAttribute('data-applies-to-left-out')}`),
    ['Lib_Retry_ true', 'Lib_Retry_ true', 'Lib_Do_ false', 'Lib_Do_ false'])
  assert.ok(!document.getElementById('methods').hidden)
})

test('in print, a section hidden for the copy shown in its place is not left out', () => {
  // the enum is described differently on .NET Standard 2.0, and Fast is on .NET 8 only
  const { document } = installDom(`${site}<body><article>
    <h1 id="Lib_Mode" data-applies-to="net8.0 netstandard2.0">Mode</h1><div class="facts"></div>
    <div class="api-contents">
      <div class="api-content" data-applies-to="net8.0"><h2 id="fields">Fields</h2><dl class="parameters">
        <dt id="Lib_Mode_Plain" data-applies-to="net8.0 netstandard2.0"><code>Plain = 0</code></dt><dd></dd>
      </dl></div>
      <div class="api-content" data-applies-to="netstandard2.0" data-search-ignore hidden><h2 id="fields_variant1">Fields</h2><dl class="parameters">
        <dt id="Lib_Mode_Plain_variant1" data-applies-to="net8.0 netstandard2.0"><code>Plain = 0</code></dt><dd></dd>
      </dl></div>
    </div>
  </article></body>`)

  applySelection({ framework: 'netstandard2.0' }, undefined, true)
  // the values are printed, in the copy, so the links to them are kept
  assert.ok(!document.getElementById('Lib_Mode_Plain_variant1').closest('[hidden]'))
  assert.deepStrictEqual(document.querySelectorAll('[data-applies-to-left-out][id], [data-applies-to-left-out] [id]'), [])
})

test('in print, the value of another axis says nothing about a page whose axis has the same ids', () => {
  // EF Core has ef8 and ef9, the drivers have ef8 too; a PDF of ef9 prints a page of the drivers
  const markup = `${site}<body><article>
    <h1 id="Driver" data-applies-to="ef8:net8.0 x:net8.0">Driver</h1><div class="facts"></div>
    <h3 id="Driver_Ef8" data-applies-to="ef8:net8.0">Ef8</h3><p>ef8 of the drivers</p>
  </article></body>`
  const print = (otherAxisPages?: string[]) => {
    const { document, window } = installDom(markup, 'https://example.org/docs/api/Driver.html')
    const docfxAppliesTo: Window['docfxAppliesTo'] = { value: 'ef9', axisValues: ['ef8', 'ef9'], otherAxisPages }
    Object.assign(window, { docfxAppliesTo })
    applyPrintSelection()
    // which `docfx pdf` reads, to warn about a template that does not
    assert.ok(docfxAppliesTo.applied)
    return !!document.getElementById('Driver_Ef8').closest('[hidden]')
  }

  assert.ok(!print(['/docs/api/Driver.html']))
  // on a page `docfx pdf` does not know the axis of, the values of the axes that have the value count
  assert.ok(print(undefined))
})

test('in print, a page that fails to apply the selection says why, and goes on', () => {
  const { document, window } = installDom(`${site}<body><article><h1 id="A" data-applies-to="net8.0">A</h1></article></body>`)
  const docfxAppliesTo: Window['docfxAppliesTo'] = { framework: 'net8.0' }
  Object.assign(window, { docfxAppliesTo })
  Object.assign(document, { querySelectorAll: () => { throw new Error('broken page') } })
  const error = console.error
  console.error = () => { }
  try {
    // which would otherwise stop the page short of ready, and `docfx pdf` wait until it times out
    applyPrintSelection()
  } finally {
    console.error = error
  }
  assert.ok(!docfxAppliesTo.applied)
  assert.strictEqual(docfxAppliesTo.error, 'broken page')
})

test('in print, the copy shown in place of the content of an API has the ids of its headings to itself', () => {
  // the remarks of .NET Standard 2.0 have a heading of their own, which markdown names as that of the API's own
  const { document, window } = installDom(`${site}<body><article>
    <h1 id="Lib_Size" data-applies-to="net8.0 netstandard2.0">Size</h1><div class="facts"></div>
    <div class="api-contents">
      <div class="api-content" data-applies-to="net8.0"><h2 id="example">Example</h2></div>
      <div class="api-content" data-applies-to="netstandard2.0" data-search-ignore hidden><h2 id="example">Example</h2></div>
    </div>
  </article></body>`)
  Object.assign(window, { docfxAppliesTo: { framework: 'netstandard2.0' } })
  applyPrintSelection()

  const printed = document.querySelectorAll('h2').filter(h => !h.closest('[hidden]'))
  assert.deepStrictEqual(printed.map(h => h.id), ['example_variant1'])
  assert.ok(document.getElementById('example').closest('[hidden]'))
})

test('in print, a section keeps what a hidden tab has', () => {
  // the second tab of a tab group is hidden until picked, and has terms of its own
  const tabs = `<h2 id="install">Install</h2>
    <div class="tabGroup"><section id="tab1"><p>dotnet add package</p></section><section id="tab2" hidden="hidden"><dl><dt>Term</dt><dd>x</dd></dl></section></div>`
  const shown = (document: { getElementById: (id: string) => { closest: (s: string) => unknown } }) =>
    ['install', 'tab1'].every(id => !document.getElementById(id).closest('[hidden]'))

  // an article in an API PDF
  const article = installDom(`${site}<body><article><h1 id="Guide">Guide</h1>${tabs}</article></body>`).document
  applySelection({ framework: 'net462' }, undefined, true)
  assert.ok(shown(article))

  // the remarks of an API page
  const api = installDom(`${site}<body><article><h1 id="Lib" data-applies-to="net8.0 net462">Lib</h1><div class="facts"></div>${tabs}</article></body>`).document
  applySelection({ framework: 'net462' }, undefined, true)
  assert.ok(shown(api))
})

test('a class derived, or a member inherited, in other frameworks only is dimmed on the page', () => {
  const { document } = installDom(printPage)
  const dimmed = (id: string) => document.getElementById(id).classList.contains('applies-to-unavailable')

  applySelection({ framework: 'net462' })
  assert.ok(!dimmed('Derived_All') && dimmed('Derived_Net8') && dimmed('Inherited_Net8'))
  assert.ok(!document.getElementById('inherited').hidden)

  applySelection({})
  assert.ok(!dimmed('Derived_Net8') && !dimmed('Inherited_Net8'))
})
