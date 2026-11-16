// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import test from 'node:test'
import assert from 'node:assert'
import {
  Axis, Selection, Toc, TocEntry, applyPrintSelection, applySelection, axisKey, canReference, chooseGroup, chooseGroups, findPath, frameworkLabel, getChoices, getSelection, hashId,
  isAvailable, keepLinkSelection, memberElements, parseUnits, rank, scrollToShownTarget, shownEntries, shownTarget, sortFrameworks, unitsOf, validFramework
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

  // a framework kept from another part of the site that this table of contents does not list is left out, and
  // forgotten, so that the next page does not apply it before its table of contents is loaded
  storage.set('docfx.axis./docs/.framework', 'net6.0')
  assert.deepStrictEqual(getSelection(ef, frameworks), { framework: undefined, value: 'ef9' })
  assert.ok(!storage.has('docfx.axis./docs/.framework'))
  // not before the table of contents is loaded, nor by one that lists no frameworks; on a page without a link's
  // framework, which would come first
  const later = installDom(site, 'https://example.org/docs/api/A.html')
  later.storage.set('docfx.axis./docs/.framework', 'net6.0')
  assert.deepStrictEqual(getSelection(ef).framework, 'net6.0')
  getSelection(ef, [])
  assert.strictEqual(later.storage.get('docfx.axis./docs/.framework'), 'net6.0')
  // so is a value that is not one of the axis
  installDom(site, 'https://example.org/docs/api/A.html?ef-core=ef99')
  assert.deepStrictEqual(getSelection(ef, frameworks), { framework: undefined, value: undefined })
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

test('one dimension picked keeps what differs in the other on the page', () => {
  const { document } = installDom(`${site}
  <article>
    <h1 id="Lib_Reader" data-applies-to="d1:net8.0 d1:netstandard2.0 d2:net8.0">Reader</h1>
    <div class="facts"></div>
    <div class="api-contents">
      <div class="api-content" data-applies-to="d1:net8.0"><p id="own">own</p></div>
      <div class="api-content" data-applies-to="d1:netstandard2.0" hidden><p id="copy1">copy1</p></div>
      <div class="api-content" data-applies-to="d2:net8.0" hidden><p id="copy2">copy2</p></div>
    </div>
    <h5 class="api-variants-heading">In other frameworks</h5>
    <div class="api-variant" id="variant1" data-applies-to="d1:netstandard2.0"></div>
    <div class="api-variant" id="variant2" data-applies-to="d2:net8.0"></div>
  </article>`)
  const hidden = (...ids: string[]) => ids.map(id => !!document.getElementById(id).closest('[hidden]'))
  const heading = () => document.querySelector<HTMLElement>('.api-variants-heading').hidden
  const all = ['own', 'copy1', 'copy2', 'variant1', 'variant2']

  applySelection({ value: 'd1' })
  assert.deepStrictEqual(hidden(...all), [false, true, true, false, true])
  assert.ok(!heading())

  applySelection({ framework: 'net8.0' })
  assert.deepStrictEqual(hidden(...all), [false, true, true, true, false])

  // Driver 2 is shown in place of the API's own content, so not under "In other frameworks" too
  applySelection({ value: 'd2' })
  assert.deepStrictEqual(hidden(...all), [true, true, false, true, true])
  assert.ok(heading())

  applySelection({ value: 'd1', framework: 'net8.0' })
  assert.deepStrictEqual(hidden(...all), [false, true, true, true, true])
  assert.ok(heading())

  applySelection({})
  assert.deepStrictEqual(hidden(...all), [false, true, true, false, false])
  assert.ok(!heading())
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
      <dl class="typelist derived"><dt>Derived</dt><dd><div id="Derived_All">All</div><div id="Derived_Net8" data-applies-to="net8.0">Net8</div></dd></dl>
      <dl class="typelist inheritedMembers" id="inherited"><dt>Inherited Members</dt><dd><div id="Inherited_Net8" data-applies-to="net8.0">Net8</div></dd></dl>
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
  // the classes derived, and the members inherited, in other frameworks only, and a list with none left
  assert.ok(!hidden('Derived_All') && hidden('Derived_Net8'))
  assert.ok(hidden('Inherited_Net8') && hidden('inherited'))
  // nothing is marked, as nothing is left to mark
  assert.deepStrictEqual(document.querySelectorAll('.applies-to-unavailable-badge, .applies-to-banner'), [])
  assert.ok(!document.body.hasAttribute('data-applies-to-unavailable'))

  // the page of an API that does not exist in the selection says so to `docfx pdf`
  applySelection({ framework: 'netcoreapp1.0' }, undefined, true)
  assert.ok(document.body.hasAttribute('data-applies-to-unavailable'))
  assert.strictEqual(document.querySelector('.applies-to-banner'), null)
})

test('in print, the value of another axis says nothing about a page whose axis has the same ids', () => {
  // EF Core has ef8 and ef9, the drivers have ef8 too; a PDF of ef9 prints a page of the drivers
  const markup = `${site}<body><article>
    <h1 id="Driver" data-applies-to="ef8:net8.0 x:net8.0">Driver</h1><div class="facts"></div>
    <h3 id="Driver_Ef8" data-applies-to="ef8:net8.0">Ef8</h3><p>ef8 of the drivers</p>
  </article></body>`
  const print = (otherAxisPages?: string[]) => {
    const { document, window } = installDom(markup, 'https://example.org/docs/api/Driver.html')
    Object.assign(window, { docfxAppliesTo: { value: 'ef9', axisValues: ['ef8', 'ef9'], otherAxisPages } })
    applyPrintSelection()
    return !!document.getElementById('Driver_Ef8').closest('[hidden]')
  }

  assert.ok(!print(['/docs/api/Driver.html']))
  // on a page `docfx pdf` does not know the axis of, the values of the axes that have the value count
  assert.ok(print(undefined))
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
