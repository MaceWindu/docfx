// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import { html, TemplateResult } from 'lit-html'
import { isExternalHref, isSameURL, loc, meta } from './helper'

/**
 * What an API is documented for: a target framework, and the value of the entry's axis, such as an EF Core
 * version, when it has one. Pages write it `value:framework` in `data-applies-to`, e.g. `ef9:net8.0`.
 */
export type Unit = { value?: string, framework: string }

/**
 * What the reader picked. A missing part is "all". `axisValues`, the ids of the axis `value` is one of, is set where
 * a page of another axis can show, as in a PDF of the API of several metadata items.
 */
export type Selection = { value?: string, framework?: string, axisValues?: string[] }

/** One entry of `appliesTo` in the table of contents, as written by `docfx metadata`. */
export type AppliesToGroup = { version?: string, label?: string, frameworks?: string[] }

enum Family { Standard, Core, Framework }

type Moniker = { family: Family, version: number[], platform?: string }

// The lowest .NET Framework version implementing each .NET Standard version that it implements at all.
const standardOnFramework: [number[], number[]][] = [
  [[1, 1], [4, 5]],
  [[1, 2], [4, 5, 1]],
  [[1, 3], [4, 6]],
  [[2, 0], [4, 6, 1]]
]

export function parseUnits(data: string): Unit[] {
  return (data || '').split(/\s+/).filter(s => s).map(s => {
    const i = s.indexOf(':')
    return i < 0 ? { framework: s } : { value: s.substring(0, i), framework: s.substring(i + 1) }
  })
}

export function unitsOf(appliesTo: AppliesToGroup[]): Unit[] {
  return (appliesTo || []).flatMap(g => (g.frameworks || []).map(framework => g.version ? { value: g.version, framework } : { framework }))
}

// The table of contents and the page compare the same few frameworks over and over.
const monikers = new Map<string, Moniker>()

function parseMoniker(moniker: string): Moniker {
  if (!monikers.has(moniker)) {
    monikers.set(moniker, parseMonikerCore(moniker))
  }
  return monikers.get(moniker)
}

function parseMonikerCore(moniker: string): Moniker {
  const match = /^(netstandard|netcoreapp|net)(\d+(?:\.\d+)*)(?:-([a-z]*))?/i.exec(moniker)
  if (!match) {
    return null
  }
  const [, name, digits, platform] = match
  const version = (digits.includes('.') ? digits.split('.') : digits.split('')).map(Number)
  switch (name.toLowerCase()) {
    case 'netstandard': return { family: Family.Standard, version, platform: platform?.toLowerCase() }
    case 'netcoreapp': return { family: Family.Core, version, platform: platform?.toLowerCase() }
    default:
      // `net8.0` is .NET 8, `net462` is .NET Framework 4.6.2, one digit per part.
      if (digits.includes('.')) {
        return { family: Family.Core, version, platform: platform?.toLowerCase() }
      }
      return digits.length === 2 || digits.length === 3 ? { family: Family.Framework, version, platform: platform?.toLowerCase() } : null
  }
}

function compareVersions(a: number[], b: number[]): number {
  for (let i = 0; i < Math.max(a.length, b.length); i++) {
    const d = (a[i] || 0) - (b[i] || 0)
    if (d !== 0) {
      return d
    }
  }
  return 0
}

/**
 * Whether a project targeting `target` could reference a library built for `framework`: a simplified form
 * of the NuGet rules, the same as `TargetFrameworkHelper` applies when `docfx metadata` picks a framework.
 */
export function canReference(target: string, framework: string): boolean {
  if (target.toLowerCase() === framework.toLowerCase()) {
    return true
  }
  const t = parseMoniker(target)
  const f = parseMoniker(framework)
  if (!t || !f || (f.platform && f.platform !== t.platform)) {
    return false
  }
  if (t.family === f.family) {
    return compareVersions(f.version, t.version) <= 0
  }
  if (t.family === Family.Core && f.family === Family.Standard) {
    const highest = compareVersions(t.version, [3, 0]) >= 0 ? [2, 1] : compareVersions(t.version, [2, 0]) >= 0 ? [2, 0] : [1, 6]
    return compareVersions(f.version, highest) <= 0
  }
  if (t.family === Family.Framework && f.family === Family.Standard) {
    return standardOnFramework.some(([standard, fx]) => compareVersions(f.version, standard) <= 0 && compareVersions(t.version, fx) >= 0)
  }
  return false
}

/**
 * How well `framework` stands in for `target`, lower is better, `Infinity` when it cannot: the target itself,
 * then the same kind of framework from the highest version down, then .NET Standard from the highest down.
 */
export function rank(target: string, framework: string): number {
  if (target.toLowerCase() === framework.toLowerCase()) {
    return 0
  }
  if (!canReference(target, framework)) {
    return Infinity
  }
  const t = parseMoniker(target)
  const f = parseMoniker(framework)
  // versions have at most 3 parts below 100, so this orders them within a family
  const weight = f.version.slice(0, 3).reduce((sum, part, i) => sum + Math.min(part, 99) * Math.pow(100, 2 - i), 0)
  return (f.family === t.family ? 1 : 2) * 1e7 - weight
}

export function matches(unit: Unit, selection: Selection): boolean {
  // a value picked for the axis of one part of the docs says nothing about a part without that axis, or with another
  const valueApplies = !!selection.value && unit.value !== undefined && (!selection.axisValues || selection.axisValues.includes(unit.value))
  return (!valueApplies || unit.value === selection.value) &&
    (!selection.framework || rank(selection.framework, unit.framework) < Infinity)
}

export function isAvailable(units: Unit[], selection: Selection): boolean {
  return units.some(u => matches(u, selection))
}

/**
 * Which content to show for a selection: groups are the units of the API's own content first, then those of
 * each variant. Returns the index of the group whose unit stands in best for the selection, the API's own
 * content on a tie, or -1 when the API does not exist in the selection.
 */
export function chooseGroup(groups: Unit[][], selection: Selection): number {
  let best = -1
  let bestRank = Infinity
  groups.forEach((units, index) => {
    for (const unit of units.filter(u => matches(u, selection))) {
      const r = selection.framework ? rank(selection.framework, unit.framework) : 0
      if (best < 0 || r < bestRank) {
        best = index
        bestRank = r
      }
    }
  })
  return best
}

/**
 * The groups to show for a selection, the one shown in place of the API's own content first, none when the API does
 * not exist in the selection. With a framework and a value picked, or a framework for an API without an axis, that is
 * the group `chooseGroup` picks. With one of them picked, it is the group that stands in best for each value of the
 * other, so that what differs between the frameworks of one axis value, or the axis values of one framework, stays.
 */
export function chooseGroups(groups: Unit[][], selection: Selection): number[] {
  const main = chooseGroup(groups, selection)
  if (main < 0) {
    return []
  }
  const best = new Map<string, { index: number, rank: number }>()
  groups.forEach((units, index) => {
    for (const unit of units.filter(u => matches(u, selection))) {
      const key = selection.framework ? (selection.value ? '' : unit.value ?? '') : unit.framework.toLowerCase()
      const r = selection.framework ? rank(selection.framework, unit.framework) : 0
      if (!best.has(key) || r < best.get(key).rank) {
        best.set(key, { index, rank: r })
      }
    }
  })
  const shown = new Set(Array.from(best.values()).map(b => b.index))
  // the API's own content has no entry under "In other frameworks" to show it in
  const first = shown.has(0) ? 0 : main
  return [first, ...Array.from(shown).filter(i => i !== first).sort((a, b) => a - b)]
}

/** Frameworks in the order a picker lists them: .NET, then .NET Standard, then .NET Framework, newest first. */
export function sortFrameworks(frameworks: string[]): string[] {
  const order = (f: string): [number, number[]] => {
    const m = parseMoniker(f)
    return m ? [m.family === Family.Core ? 0 : m.family === Family.Standard ? 1 : 2, m.version] : [3, []]
  }
  return Array.from(new Set(frameworks)).sort((a, b) => {
    const [fa, va] = order(a)
    const [fb, vb] = order(b)
    return fa - fb || compareVersions(vb, va) || a.localeCompare(b)
  })
}

/**
 * The axis of a part of the docs, as `docfx metadata` writes it to the root of its table of contents and to each of
 * its entries, which a table of contents that includes it keeps.
 */
export type Axis = { name: string, values: { id: string, label: string }[] }

/** An entry of the table of contents, with what the picker reads from it. */
export type TocEntry = { name?: string, href?: string, appliesTo?: AppliesToGroup[], axis?: Axis, items?: TocEntry[] }

/** A table of contents as `toc.json` has it. */
export type Toc = { items?: TocEntry[], axis?: Axis, frameworkNames?: Record<string, string> }

/**
 * What a reader can pick on a page: the frameworks of its table of contents, and the axis of its part of the docs;
 * `axes` are those of every part, whose entries the table of contents filters by the value picked for each.
 */
export type Choices = { frameworks: string[], axis?: Axis, key?: string, axes?: Axis[] }

const changeEvent = 'docfx:appliestochange'

export function isPickerEnabled(): boolean {
  return meta('docfx:appliestopicker') === 'true'
}

// The keys of the query string the template reads itself: the frameworks, the tabs a link picks, and the search a
// result highlights.
const reservedKeys = ['framework', 'tabs', 'q']

/**
 * The key of an axis in storage and in the query string: its name in lower case, its letters and digits in any
 * script, e.g. `ef-core` or `версия`; `_axis` for a name without any; with `_axis` after a key the template reads
 * itself, e.g. `framework_axis` or `tabs_axis`. A name never gives an underscore, so no other name has those keys.
 */
export function axisKey(axis: Axis): string {
  const key = axis.name.toLowerCase().replace(/[^\p{L}\p{N}]+/gu, '-').replace(/^-|-$/g, '')
  return reservedKeys.includes(key) ? `${key}_axis` : key || '_axis'
}

/**
 * The framework a link or storage names, as the table of contents spells it; nothing for one it does not list,
 * which would leave out every API. Before the table of contents is loaded, any framework docfx can compare.
 */
export function validFramework(framework: string, frameworks?: string[]): string | undefined {
  if (!frameworks) {
    return parseMoniker(framework) ? framework : undefined
  }
  return frameworks.find(f => f.toLowerCase() === framework.toLowerCase())
}

/**
 * The key a choice is kept under. Sites on one origin, such as the projects of one github.io account, share local
 * storage, so the key includes the root of the site.
 */
function storageKey(key: string): string {
  const root = new URL(meta('docfx:appliestorel') || './', window.location.href).pathname
  return `docfx.axis.${root}.${key}`
}

/**
 * What the reader picked: from the query string, for shareable links, else what they picked last. A value that is
 * not one of the axis, or a framework the table of contents does not list, is left out, so that a typo in a link, or
 * a choice kept from another part or an earlier version of the site, does not hide everything. What was picked last
 * is kept as it is, not forgotten: it applies again in the part of the site that lists it, and another tab, open on a
 * part that does not, would otherwise undo the pick the reader just made.
 */
export function getSelection(axis?: Axis, frameworks?: string[]): Selection {
  const query = new URLSearchParams(window.location.search)
  const read = (key: string, valid: (value: string) => string | undefined) =>
    [query.get(key), localStorage?.getItem(storageKey(key))].map(value => value ? valid(value) : undefined).find(value => value)
  return {
    framework: read('framework', f => validFramework(f, frameworks)),
    value: axis ? read(axisKey(axis), v => axis.values.some(a => a.id === v) ? v : undefined) : undefined
  }
}

/**
 * Keeps the choice a link made, so that it carries on to the pages the reader goes to next: the framework, and the
 * value of each axis it names, that of another part of the docs too, whose entries the table of contents filters by
 * it. Only when the link is followed: a page reloaded, or shown again from the history of the tab or a restored
 * session, would otherwise undo what the reader picked since.
 */
export function keepLinkSelection(choices: Choices) {
  if (!isFollowedLink()) {
    return
  }
  const query = new URLSearchParams(window.location.search)
  const selection = getSelection(choices.axis, choices.frameworks)
  if (selection.framework && query.get('framework')?.toLowerCase() === selection.framework.toLowerCase()) {
    localStorage?.setItem(storageKey('framework'), selection.framework)
  }
  const axes = [choices.axis, ...(choices.axes || [])].filter(a => a)
  for (const key of new Set(axes.map(axisKey))) {
    const value = query.get(key)
    if (value && axes.some(a => axisKey(a) === key && a.values.some(v => v.id === value))) {
      localStorage?.setItem(storageKey(key), value)
    }
  }
}

/** Whether the page was opened by following a link or entering its address, rather than reloaded or gone back to. */
function isFollowedLink(): boolean {
  const entry = window.performance?.getEntriesByType?.('navigation')?.[0] as PerformanceNavigationTiming | undefined
  return !entry || (entry.type !== 'reload' && entry.type !== 'back_forward')
}

/** Keeps what the reader picked in a dropdown, `undefined` for "All", and shows the page for it. */
export function select(key: string, value: string | undefined) {
  if (value) {
    localStorage?.setItem(storageKey(key), value)
  } else {
    localStorage?.removeItem(storageKey(key))
  }
  // the query string would override the choice on this page
  const url = new URL(window.location.href)
  if (url.searchParams.has(key)) {
    url.searchParams.delete(key)
    history.replaceState(null, '', url)
  }
  window.dispatchEvent(new Event(changeEvent))
}

export function onSelectionChange(callback: () => void) {
  window.addEventListener(changeEvent, callback)
}

type TocSummary = { frameworks: string[], axes: Axis[] }

// The table of contents is rendered again on every click and keystroke in its filter, and can have thousands of entries.
const summaries = new WeakMap<Toc, TocSummary>()

function summarize(toc: Toc): TocSummary {
  if (!summaries.has(toc)) {
    const frameworks: string[] = []
    const axes: Axis[] = []
    // Parts of a merged table of contents can each have an axis of the same name, which is one choice of the reader:
    // with the values of all of them, so that a value only another part has can be picked and kept too. Names that
    // differ only in what their key leaves out, such as letter case, share the key, so they are one axis too.
    const add = (axis: Axis) => {
      const index = axes.findIndex(a => axisKey(a) === axisKey(axis))
      if (index < 0) {
        axes.push(axis)
        return
      }
      const values = axis.values.filter(v => !axes[index].values.some(a => a.id === v.id))
      if (values.length > 0) {
        axes[index] = { ...axes[index], values: [...axes[index].values, ...values] }
      }
    }
    if (toc.axis) {
      add(toc.axis)
    }
    const visit = (items: TocEntry[]) => items?.forEach(item => {
      unitsOf(item.appliesTo).forEach(u => frameworks.push(u.framework))
      if (item.axis) {
        add(item.axis)
      }
      visit(item.items)
    })
    visit(toc.items)
    summaries.set(toc, { frameworks: sortFrameworks(frameworks), axes })
  }
  return summaries.get(toc)
}

/**
 * What a reader can pick on a page: the frameworks of its table of contents, and the axis of the part of the docs
 * the page is in, given by the entries from the root down to the page; on another page, such as an article, the axis when
 * there is one only. An API page of a part without an axis has none, even when another part has one.
 */
export function getChoices(toc: Toc, path: TocEntry[] = []): Choices {
  const { frameworks, axes } = summarize(toc)
  const axis = [...path].reverse().find(entry => entry.axis)?.axis || toc.axis ||
    (axes.length === 1 && !path.some(entry => hasData(entry)) ? axes[0] : undefined)
  return { frameworks, axis, key: axis ? axisKey(axis) : undefined, axes }
}

/** The entries from the root down to the entry of the page at `url`, none when the table of contents has no such entry. */
export function findPath(items: TocEntry[], tocUrl: URL, url: { pathname: string }): TocEntry[] {
  for (const item of items || []) {
    if (item.href) {
      const href = new URL(item.href, tocUrl)
      if (!isExternalHref(href) && isSameURL(href, url)) {
        return [item]
      }
    }
    const path = findPath(item.items, tocUrl, url)
    if (path.length > 0) {
      return [item, ...path]
    }
  }
  return []
}

/**
 * The selection for the entries below `entry`: an entry with an axis of its own, such as the root of the API of a
 * table of contents that includes several, has the value picked for that axis.
 */
export function entrySelection(entry: TocEntry, selection: Selection, frameworks: string[]): Selection {
  return entry.axis ? { framework: selection.framework, value: getSelection(entry.axis, frameworks).value } : selection
}

/**
 * Whether a table of contents entry says what it applies to: `docfx metadata` writes no `appliesTo` for an entry
 * that is not an API, and a hand-written `appliesTo: []` says nothing either, so its entry is shown.
 */
function hasData(entry: TocEntry): boolean {
  return (entry.appliesTo?.length ?? 0) > 0
}

/** Whether a table of contents entry exists in what the reader picked. */
export function isEntryShown(entry: TocEntry, selection: Selection): boolean {
  return !hasData(entry) || !(selection.framework || selection.value) || isAvailable(unitsOf(entry.appliesTo), selection)
}

/**
 * The entries with a link that the table of contents shows for the selection, in order: those that exist in it, and
 * the page being read and its parents, which `keep` names.
 */
export function shownEntries(items: TocEntry[], selection: Selection, frameworks: string[], keep: (entry: TocEntry) => boolean): TocEntry[] {
  return (items || []).flatMap(entry => {
    const own = entrySelection(entry, selection, frameworks)
    if (!keep(entry) && !isEntryShown(entry, own)) {
      return hasShownApi(entry.items, own, frameworks) ? shownEntries(entry.items, own, frameworks, keep) : []
    }
    return [...(entry.href ? [entry] : []), ...shownEntries(entry.items, own, frameworks, keep)]
  })
}

/**
 * Whether an entry among `items`, or below them, is an API that exists in the selection. A namespace whose types
 * exist in fewer frameworks than those of a namespace in it, which `namespaceLayout: nested` puts below it, is the
 * heading of that namespace still.
 */
function hasShownApi(items: TocEntry[], selection: Selection, frameworks: string[]): boolean {
  return (items || []).some(entry => {
    const own = entrySelection(entry, selection, frameworks)
    return (hasData(entry) && isEntryShown(entry, own)) || hasShownApi(entry.items, own, frameworks)
  })
}

/**
 * How the table of contents shows an entry for the selection of its parent: not at all, without entries below it,
 * with them, or as the heading of those entries that exist in the selection when the entry itself does not, without
 * its page. A group whose entries the selection all leaves out goes too, or without them when it has a page.
 */
export function entryDisplay(entry: TocEntry, selection: Selection, frameworks: string[], keep: (entry: TocEntry) => boolean): 'none' | 'leaf' | 'group' | 'heading' {
  const own = entrySelection(entry, selection, frameworks)
  if (!keep(entry) && !isEntryShown(entry, own)) {
    return hasShownApi(entry.items, own, frameworks) ? 'heading' : 'none'
  }
  if (!entry.items?.length) {
    return 'leaf'
  }
  if ((own.framework || own.value) && !entry.items.some(child => child.name && entryDisplay(child, own, frameworks, keep) !== 'none')) {
    return entry.href ? 'leaf' : 'none'
  }
  return 'group'
}

const tocs = new Map<string, Promise<unknown>>()

// `_frameworkNames`, which the table of contents carries for the pages that are not API pages
let tocFrameworkNames: Record<string, string> = {}

/** The table of contents at `url`, fetched once for the table of contents, the navbar and the picker. */
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function fetchToc(url: URL): Promise<any> {
  if (!tocs.has(url.href)) {
    tocs.set(url.href, fetch(url).then(res => res.json()).then((toc: Toc) => {
      tocFrameworkNames = toc.frameworkNames || {}
      return toc
    }))
  }
  return tocs.get(url.href)
}

/** The choices of the current table of contents, none when the picker is off. */
export async function loadChoices(): Promise<Choices> {
  const tocrel = meta('docfx:tocrel')
  if (!isPickerEnabled() || !tocrel) {
    return { frameworks: [] }
  }
  const tocUrl = new URL(tocrel.replace(/.html$/gi, '.json'), window.location.href)
  const toc: Toc = await fetchToc(tocUrl)
  return getChoices(toc, findPath(toc.items, tocUrl, window.location))
}

/**
 * Applies the selection to the page now and whenever the reader picks another, then calls `onApplied`, which
 * updates what depends on the content shown, such as the headings of "In this article".
 */
export async function initAppliesTo(onApplied: () => void = () => { }) {
  // The framework can be applied before the table of contents is loaded, so that the page does not show all
  // frameworks first; the value of an axis needs the axis, which the table of contents names. Only a framework the
  // page names, though: one kept from another part of the site, which this table of contents does not list, would show
  // first, then all frameworks, on every page.
  const early = isPickerEnabled() ? getSelection() : {}
  if (early.framework && !namesFramework(early.framework)) {
    early.framework = undefined
  }
  if (early.framework) {
    applySelection(early)
    onApplied()
  }

  const choices = await loadChoices().catch((): Choices => ({ frameworks: [] }))
  if (choices.frameworks.length === 0) {
    if (early.framework) {
      applySelection({})
      onApplied()
    }
    return
  }

  keepLinkSelection(choices)
  const apply = () => {
    applySelection(getSelection(choices.axis, choices.frameworks), choices.axis)
    onApplied()
  }
  apply()
  // What the selection shows moves the element a link points to, which the page scrolled to before; and a link on
  // the page to content hidden for the selection, such as a value of an enum in a variant shown in place of the API's
  // own content, points to nothing the browser can scroll to.
  scrollToShownTarget()
  window.addEventListener('hashchange', () => scrollToShownTarget(true))
  window.addEventListener('click', (e: Event) => {
    // a link to the fragment already in the address changes nothing, so the browser does not say so
    const link = (e.target as Element)?.closest?.('a[href*="#"]') as HTMLAnchorElement
    if (link && link.hash === window.location.hash && link.pathname === window.location.pathname) {
      setTimeout(() => scrollToShownTarget(true))
    }
  })
  onSelectionChange(apply)
  // A page the browser kept for its back button, or open in another tab, shows what was picked when it was last
  // shown; the reader may have picked something else since, on another page. A choice in its address still wins, so a
  // link shows what it names without local storage too.
  const changed = () => window.dispatchEvent(new Event(changeEvent))
  window.addEventListener('pageshow', (e: PageTransitionEvent) => {
    if (e.persisted) {
      changed()
    }
  })
  window.addEventListener('storage', (e: StorageEvent) => {
    if (!e.key || e.key.startsWith('docfx.axis.')) {
      changed()
    }
  })
}

/** Whether an API of the page is documented for `framework` itself. */
function namesFramework(framework: string): boolean {
  const name = framework.toLowerCase()
  return Array.from(document.querySelectorAll<HTMLElement>('[data-applies-to]'))
    .some(e => parseUnits(e.dataset.appliesTo).some(u => u.framework.toLowerCase() === name))
}

/**
 * Scrolls to the element shown for the fragment of the address, if it has one; with `onlyHidden`, only when the
 * element it names is hidden, which the browser cannot scroll to.
 */
export function scrollToShownTarget(onlyHidden = false) {
  if (!window.location.hash) {
    return
  }
  const id = hashId(window.location.hash)
  if (!onlyHidden || document.getElementById(id)?.closest('[hidden]')) {
    shownTarget(id)?.scrollIntoView()
  }
}

/** The id a fragment such as `#Lib_Run%2A` points to; the fragment as it is when it is not valid percent-encoding. */
export function hashId(hash: string): string {
  const id = hash.replace(/^#/, '')
  try {
    return decodeURIComponent(id)
  } catch {
    return id
  }
}

/**
 * The element shown for the id a link points to: when the content it is in is hidden for the selection, the same
 * element in the content shown instead, whose id has a suffix such as `_variant1` in a copy.
 */
export function shownTarget(id: string): Element | null {
  const target = document.getElementById(id)
  const container = target?.closest('.api-contents')
  if (!target || !container || !target.closest('[hidden]')) {
    return target
  }
  const blocks = Array.from(container.children).filter(e => e.classList.contains('api-content'))
  const base = withoutCopySuffix(id, blocks.indexOf(target.closest('.api-content')))
  for (const [index, block] of blocks.entries()) {
    if (!(block as HTMLElement).hidden) {
      const same = Array.from(block.querySelectorAll('[id]')).find(e => withoutCopySuffix(e.id, index) === base)
      if (same) {
        return same
      }
    }
  }
  return target
}

/**
 * An id of the content of an API at `index` among its copies, without the suffix of that copy: `_variant1` for the
 * first, last in the id or before the part a template adds, such as `_remarks`. A member named like a suffix, such as
 * `Get_variant2`, keeps its name.
 */
function withoutCopySuffix(id: string, index: number): string {
  if (index <= 0) {
    return id
  }
  const suffix = `_variant${index}`
  for (let i = id.lastIndexOf(suffix); i >= 0; i = i > 0 ? id.lastIndexOf(suffix, i - 1) : -1) {
    const end = i + suffix.length
    if (end === id.length || id[end] === '_') {
      return id.substring(0, i) + id.substring(end)
    }
  }
  return id
}

/**
 * The dropdowns of the navbar, one for the axis of this part of the docs, if it has one, and one for frameworks.
 * Nothing when the picker is off or the table of contents applies to no framework.
 */
export function appliesToPicker(choices: Choices): TemplateResult {
  if (!isPickerEnabled() || choices.frameworks.length === 0) {
    return null
  }
  const names = frameworkNames()
  const selection = getSelection(choices.axis, choices.frameworks)
  // The button shows what is picked, such as ".NET 8", and is named with what it picks too, which a screen reader
  // would not say otherwise: "Framework: .NET 8".
  const dropdown = (key: string, title: string, name: string, current: string, selected: string | undefined, items: { id: string, label: string }[]) => html`
    <div class='dropdown applies-to-picker'>
      <button type='button' title='${title}' aria-label='${name}' class='btn border-0 dropdown-toggle' data-bs-toggle='dropdown' aria-expanded='false'>${current}</button>
      <ul class='dropdown-menu dropdown-menu-end'>
        ${items.map(i => html`<li><a class='dropdown-item ${i.id === selected ? 'active' : ''}' aria-current=${i.id === selected ? 'true' : 'false'} href='#'
          @click=${(e: Event) => { e.preventDefault(); select(key, i.id) }}>${i.label}</a></li>`)}
      </ul>
    </div>`

  // "Framework: .NET 8", by a token, as the colon is written differently in some languages
  const label = (name: string, value: string) => loc('appliesToPickerLabel', { name, value })
  const axis = choices.axis
  const value = axis?.values.find(v => v.id === selection.value)?.label
  const axisDropdown = axis
    ? dropdown(choices.key, axis.name, label(axis.name, value || loc('appliesToAll')),
      // a value's label already names the axis ("EF Core 3.1"), "All" does not
      value || label(axis.name, loc('appliesToAll')),
      selection.value,
      [{ id: undefined, label: loc('appliesToAll') }, ...axis.values])
    : null
  const framework = selection.framework ? frameworkLabel(selection.framework, names) : loc('appliesToAllFrameworks')
  const frameworkDropdown = dropdown('framework', loc('appliesToFramework'), label(loc('appliesToFramework'), framework),
    framework,
    selection.framework,
    [{ id: undefined, label: loc('appliesToAllFrameworks') }, ...choices.frameworks.map(f => ({ id: f, label: frameworkLabel(f, names) }))])
  return html`${axisDropdown}${frameworkDropdown}`
}

function frameworkNames(): Record<string, string> {
  try {
    return { ...tocFrameworkNames, ...JSON.parse(meta('docfx:frameworknames') || '{}') }
  } catch {
    return tocFrameworkNames
  }
}

/** Describes a selection in a banner or a badge, e.g. `EF Core 3.1, .NET Framework 4.6.2`. */
function selectionLabel(selection: Selection, axis?: Axis): string {
  const value = axis?.values.find(v => v.id === selection.value)?.label
  const framework = selection.framework ? frameworkLabel(selection.framework, frameworkNames()) : undefined
  return [value, framework].filter(s => s).join(', ')
}

/**
 * Shows the page for the selection: the content of each API for the framework that stands in best, and of what
 * differs elsewhere only what differs within the selection; members, types, and inherited members and extension
 * methods, that do not exist in the selection dimmed; "Applies to" for what is shown; a banner when the API itself
 * does not exist in it. In print, for a PDF of one framework, such members are left out instead.
 */
export function applySelection(selection: Selection, axis?: Axis, print = false) {
  const any = !!(selection.framework || selection.value)
  const label = selectionLabel(selection, axis)

  // the variants under "In other frameworks" that follow the content of an API, and those of them still shown
  const listed = new Set<Element>()
  const kept = new Set<Element>()
  document.querySelectorAll('.api-contents').forEach(container => {
    const blocks = Array.from(container.children).filter(e => e.classList.contains('api-content')) as HTMLElement[]
    const shown = any ? chooseGroups(blocks.map(b => parseUnits(b.dataset.appliesTo)), selection) : blocks.map((_, i) => i)
    const inPlace = shown.length > 0 ? shown[0] : 0
    blocks.forEach((block, i) => { block.hidden = i !== inPlace })
    // the copy of each variant follows the API's own content, in the order "In other frameworks" lists them
    const variants = variantsAfter(container)
    variants.forEach(v => listed.add(v))
    shown.filter(i => i > 0 && i !== inPlace).forEach(i => variants[i - 1] && kept.add(variants[i - 1]))
  })
  document.querySelectorAll<HTMLElement>('.api-variant').forEach(e => { e.hidden = listed.has(e) ? !kept.has(e) : any })
  document.querySelectorAll<HTMLElement>('.api-variants-heading').forEach(heading => {
    const variants: HTMLElement[] = []
    for (let e = heading.nextElementSibling; e?.classList.contains('api-variant'); e = e.nextElementSibling) {
      variants.push(e as HTMLElement)
    }
    heading.hidden = variants.length > 0 ? variants.every(v => v.hidden) : any
  })

  document.querySelectorAll('.applies-to-unavailable-badge, .applies-to-banner, .applies-to-selection').forEach(e => e.remove())
  document.querySelectorAll('.applies-to-unavailable').forEach(e => e.classList.remove('applies-to-unavailable'))
  const line = document.querySelector<HTMLElement>('article .applies-to-line dd')
  if (line) {
    line.hidden = false
  }
  if (!any) {
    return
  }

  // members, and the values of an enum, which are a list of their own
  document.querySelectorAll<HTMLElement>('article h3[data-applies-to], article dt[data-applies-to]').forEach(heading => {
    if (isAvailable(parseUnits(heading.dataset.appliesTo), selection)) {
      return
    }
    if (print) {
      for (const e of memberElements(heading)) {
        (e as HTMLElement).hidden = true
      }
      return
    }
    heading.appendChild(unavailableBadge(label, false))
    for (const e of memberElements(heading)) {
      e.classList.add('applies-to-unavailable')
    }
  })

  // The entries of a list are marked as members are, not only dimmed, which a reader who cannot tell the colours
  // apart, or does not see them, would miss.
  // the types a namespace page lists, and the members of a type with a page each
  document.querySelectorAll<HTMLElement>('article dl.jumplist[data-applies-to]').forEach(entry => {
    if (!isAvailable(parseUnits(entry.dataset.appliesTo), selection)) {
      if (print) {
        entry.hidden = true
      } else {
        entry.classList.add('applies-to-unavailable')
        entry.querySelector('dt')?.appendChild(unavailableBadge(label, true))
      }
    }
  })

  // the classes derived, the interfaces implemented, and the members inherited, in only some frameworks of a type,
  // as a base type differs, and
  // extension methods declared in only some, which can be in another part of the docs, with another axis or none
  document.querySelectorAll<HTMLElement>('article dl.typelist').forEach(list => {
    list.querySelectorAll<HTMLElement>('dd div[data-applies-to]').forEach(entry => {
      if (!isAvailable(unitsOnAxis(parseUnits(entry.dataset.appliesTo), axis), selection)) {
        entry.classList.add('applies-to-unavailable')
        entry.appendChild(unavailableBadge(label, true))
      }
    })
  })

  // In print, a section whose members are all left out goes too: "Events" on a type page, or "Interfaces" on a
  // namespace page, where the headings of such groups are `h3`s without frameworks of their own.
  if (print) {
    document.querySelectorAll<HTMLElement>('article h2, article h3:not([data-applies-to])').forEach(section => {
      const ends = section.tagName === 'H2' ? /^H[12]$/ : /^H[1-3]$/
      const following: HTMLElement[] = []
      for (let e = section.nextElementSibling; e && !ends.test(e.tagName); e = e.nextElementSibling) {
        following.push(e as HTMLElement)
      }
      const members = following.flatMap(e => e.matches('h3, dt, dl.jumplist') ? [e] : Array.from(e.querySelectorAll<HTMLElement>('dt')))
      if (members.length > 0 && members.every(m => m.closest('[hidden]'))) {
        section.hidden = true
        following.forEach(e => { e.hidden = true })
      }
    })
  }

  // A PDF of one framework leaves out the pages of APIs that do not exist in it, by their entries in the table of
  // contents; `docfx pdf` warns about a page it printed all the same. It keeps "Applies to" as it is: it has no axis
  // to label a value with.
  const title = document.querySelector<HTMLElement>('article h1[data-applies-to]')
  const units = title ? parseUnits(title.dataset.appliesTo) : []
  const unavailable = !!title && !isAvailable(units, selection)
  if (print) {
    document.body?.toggleAttribute('data-applies-to-unavailable', unavailable)
  } else if (unavailable) {
    const banner = document.createElement('div')
    banner.className = 'applies-to-banner alert alert-warning'
    banner.textContent = loc('appliesToUnavailable', { selection: label })
    // after the namespace and Applies to of a type or member, or the title of a namespace, which has neither
    const before = title.parentElement.querySelector('.facts') || title
    before.after(banner)
  } else if (title && line && (axis || !units.some(u => u.value))) {
    // Without the axis, before the table of contents that names it is loaded, the line stays as the page has it
    // rather than show the ids of its values. With `_disableApiAppliesToFrameworks`, the line names the axis values only.
    const withFrameworks = !line.parentElement?.hasAttribute('data-frameworks-hidden')
    line.hidden = true
    line.after(shownLine(units, selection, axis, withFrameworks))
  }
}

/**
 * The units of an entry as the value picked for `axis` reads them: a value of another axis, such as that of an
 * extension method declared in another part of the docs, says nothing about it, so only the framework counts.
 */
function unitsOnAxis(units: Unit[], axis?: Axis): Unit[] {
  return axis ? units.map(u => u.value === undefined || axis.values.some(v => v.id === u.value) ? u : { framework: u.framework }) : units
}

/**
 * "Applies to" for a selection: what the content shown is documented for, the framework that stands in best for
 * each axis value, or with a value only, the frameworks of that value; e.g. `**EF Core 9** (.NET 8)`.
 */
function shownLine(units: Unit[], selection: Selection, axis: Axis | undefined, withFrameworks: boolean): HTMLElement {
  const groups = new Map<string, Unit[]>()
  for (const unit of units.filter(u => matches(u, selection))) {
    const key = unit.value ?? ''
    const group = groups.get(key) || []
    if (!selection.framework) {
      group.push(unit)
    } else if (group.length === 0 || rank(selection.framework, unit.framework) < rank(selection.framework, group[0].framework)) {
      group.splice(0, 1, unit)
    }
    groups.set(key, group)
  }

  const names = frameworkNames()
  const dd = document.createElement('dd')
  dd.className = 'applies-to-selection'
  Array.from(groups.entries()).forEach(([value, group], i) => {
    const frameworks = group.map(u => frameworkLabel(u.framework, names)).join(', ')
    if (i > 0) {
      dd.append(', ')
    }
    if (value) {
      const strong = document.createElement('strong')
      strong.textContent = axis?.values.find(v => v.id === value)?.label || value
      dd.append(strong)
      if (withFrameworks) {
        dd.append(` (${frameworks})`)
      }
    } else {
      dd.append(frameworks)
    }
  })
  return dd
}

/**
 * Whether the selection hides an element: in the content of an API for other frameworks, or in what differs elsewhere.
 * Content hidden otherwise, such as a tab not picked, is not the selection's.
 */
export function isHiddenBySelection(element: Element): boolean {
  return !!element.closest('.api-content[hidden], .api-variant[hidden], .api-variants-heading[hidden]')
}

/** The badge of something that does not exist in the selection; smaller in a list than in a heading. */
function unavailableBadge(label: string, inList: boolean): HTMLElement {
  const badge = document.createElement('span')
  badge.className = `applies-to-unavailable-badge badge text-bg-secondary fw-normal ms-2${inList ? '' : ' fs-6'}`
  badge.textContent = loc('appliesToUnavailable', { selection: label })
  return badge
}

/** The elements of "In other frameworks" after the content of an API, one for each variant. */
function variantsAfter(contents: Element): Element[] {
  const heading = contents.nextElementSibling
  const variants: Element[] = []
  if (heading?.classList.contains('api-variants-heading')) {
    for (let e = heading.nextElementSibling; e?.classList.contains('api-variant'); e = e.nextElementSibling) {
      variants.push(e)
    }
  }
  return variants
}

/**
 * The elements of a member: its heading and what follows up to the next heading or member, or for the value of an
 * enum, its term and description.
 */
export function memberElements(heading: Element): Element[] {
  const elements = [heading]
  for (let e = heading.nextElementSibling; e; e = e.nextElementSibling) {
    const ends = heading.tagName === 'DT'
      ? e.tagName !== 'DD'
      : /^H[1-3]$/.test(e.tagName) || e.hasAttribute('data-overload') || isSeeAlsoContents(e)
    if (ends) {
      break
    }
    elements.push(e)
  }
  return elements
}

const isSeeAlso = (h: Element) => h.tagName === 'H2' && /^seealso(_variant\d+)?$/.test(h.id)

/** The see-also links of a page whose variants have links of their own, which come in a content of their own for each. */
function isSeeAlsoContents(e: Element): boolean {
  return e.classList.contains('api-contents') && Array.from(e.querySelectorAll('h2')).some(isSeeAlso)
}

/**
 * Gives what the hidden copy of an API repeats of the API's own content ids of its own, with the suffix of the copy,
 * such as `_variant1`, which `shownTarget` maps: the headings of its summary and remarks, which have the ids of the
 * API's own, and its headings without an id, such as "Parameters", as the same heading of the API's own content is
 * named. Run after the headings of the page are named, and before those of the copies, which would otherwise take the
 * numbers that the headings after them have without copies.
 */
export function distinguishCopies() {
  document.querySelectorAll('.api-contents').forEach(container => {
    const blocks = Array.from(container.children).filter(e => e.classList.contains('api-content'))
    blocks.slice(1).forEach((copy, i) => {
      const suffix = `_variant${i + 1}`
      copy.querySelectorAll('[id]').forEach(e => {
        if (document.getElementById(e.id) !== e) {
          e.id += suffix
        }
      })
      const seen = new Map<string, number>()
      copy.querySelectorAll('h2, h3, h4').forEach(heading => {
        if (heading.id) {
          return
        }
        const text = heading.textContent.trim()
        const key = `${heading.tagName} ${text}`
        const n = seen.get(key) || 0
        seen.set(key, n + 1)
        const own = Array.from(blocks[0].querySelectorAll(heading.tagName)).filter(h => h.textContent.trim() === text)[n]
        if (own?.id) {
          heading.id = own.id + suffix
        }
      })
    })
  })
}

/**
 * Gives the headings of the article their anchors with `add`, AnchorJS's: those of the page first, so that they are
 * named as on a page without copies, then those of the hidden copies of an API for other frameworks, which are named
 * after the page's own. Filtered here rather than by `:not([data-search-ignore] *)`, which browsers before 2021 do not
 * read, and which would leave a page without anchors there.
 */
export function addHeadingAnchors(add: (headings: Element[]) => void) {
  const headings = Array.from(document.querySelectorAll('article h2:not(.no-anchor), article h3:not(.no-anchor), article h4:not(.no-anchor)'))
  const inCopy = (heading: Element) => heading.closest('[data-search-ignore]') !== null
  add(headings.filter(heading => !inCopy(heading)))
  distinguishCopies()
  add(headings.filter(inCopy))
}

/**
 * The link of a table of contents entry shown as `display`: none for the heading of entries that exist in the
 * selection when the entry itself does not, as its page has nothing for the selection.
 */
export function entryHref(entry: TocEntry, display: 'leaf' | 'group' | 'heading'): string | undefined {
  return display === 'heading' ? undefined : entry.href
}

declare global {
  interface Window {
    /**
     * Set by `docfx pdf` for a PDF of one framework or axis value: `pdfFramework`, `pdfAxisValue`, and the ids of
     * the axis that value is one of, when the table of contents names it.
     */
    docfxAppliesTo?: { framework?: string, value?: string, axisValues?: string[] }
  }
}

/** In a PDF of one framework or axis value, shows the pages for it. */
export function applyPrintSelection() {
  const selection = window.docfxAppliesTo
  if (selection) {
    applySelection({ framework: selection.framework || undefined, value: selection.value || undefined, axisValues: selection.axisValues || undefined }, undefined, true)
  }
}

/** The display name of a framework, as the templates write it in Applies to. */
export function frameworkLabel(framework: string, names?: Record<string, string>): string {
  if (names && Object.prototype.hasOwnProperty.call(names, framework)) {
    return names[framework]
  }
  let match = /^netstandard(\d+\.\d+)$/.exec(framework)
  if (match) {
    return `.NET Standard ${match[1]}`
  }
  match = /^netcoreapp(\d+\.\d+)$/.exec(framework)
  if (match) {
    return `.NET Core ${match[1]}`
  }
  match = /^net(\d+)\.(\d+)(?:-([a-z]+)([\d.]*))?$/i.exec(framework)
  if (match) {
    const name = `.NET ${match[1]}${match[2] === '0' ? '' : '.' + match[2]}`
    return match[3] ? `${name} for ${platformLabel(match[3])}${match[4] ? ' ' + match[4] : ''}` : name
  }
  match = /^net(\d)(\d)(\d)?$/.exec(framework)
  if (match) {
    return `.NET Framework ${match[1]}.${match[2]}${match[3] ? '.' + match[3] : ''}`
  }
  return framework
}

const platformLabels: Record<string, string> = {
  android: 'Android', browser: 'Browser', ios: 'iOS', maccatalyst: 'Mac Catalyst', macos: 'macOS', tizen: 'Tizen', tvos: 'tvOS', wasi: 'WASI', windows: 'Windows'
}

/** The name of a platform of a framework, as the templates write it: `windows` is Windows. */
function platformLabel(platform: string): string {
  const key = platform.toLowerCase()
  return Object.prototype.hasOwnProperty.call(platformLabels, key) ? platformLabels[key] : key.charAt(0).toUpperCase() + key.slice(1)
}
