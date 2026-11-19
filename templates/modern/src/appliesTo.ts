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
 * a page of another axis can show, as in a PDF of the API of several metadata items. `within` limits it to some units,
 * those of the copy of a variant shown whole that the pick cannot tell apart, as `narrowSelection` reads them. `part`
 * is what the part of the docs the API is in is built for, by which a framework picked stands for the one of them a
 * project targeting it gets.
 */
export type Selection = { value?: string, framework?: string, axisValues?: string[], within?: Unit[], part?: Unit[] }

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
  // Only the names MSBuild writes, as `TargetFrameworkHelper` reads them: a version of two to four parts, so
  // `netstandard2` or `net8` is no framework it knows, on the page as in `docfx metadata` and `docfx pdf`.
  if (digits.includes('.') && !/^\d+(?:\.\d+){1,3}$/.test(digits)) {
    return null
  }
  switch (name.toLowerCase()) {
    case 'netstandard': return digits.includes('.') ? { family: Family.Standard, version, platform: platform?.toLowerCase() } : null
    case 'netcoreapp': return digits.includes('.') ? { family: Family.Core, version, platform: platform?.toLowerCase() } : null
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
 * Whether two framework names name the same framework, either way round with the platform version one leaves out,
 * as `TargetFrameworkHelper.IsSameFramework`: `net8.0-windows` is the project loaded as `net8.0-windows10.0.19041.0`.
 */
function isSameFramework(left: string, right: string): boolean {
  return left.toLowerCase() === right.toLowerCase() || matchesFramework(left, right) || matchesFramework(right, left)
}

function matchesFramework(project: string, target: string): boolean {
  const p = frameworkParts(project)
  const t = frameworkParts(target)
  return p.framework === t.framework && p.platform === t.platform && (t.platformVersion === undefined || t.platformVersion === p.platformVersion)
}

// The framework without its platform, the platform without its version, and the platform version with the parts it
// leaves out as zero, as `TargetFrameworkHelper` reads them: `android34` is 34.0.0.0, a version it cannot read none.
function frameworkParts(name: string): { framework: string, platform?: string, platformVersion?: string } {
  const dash = name.indexOf('-')
  if (dash < 0) {
    return { framework: name.toLowerCase() }
  }
  const rest = name.substring(dash + 1)
  const platform = /^[a-z]*/i.exec(rest)[0]
  const digits = rest.substring(platform.length)
  const platformVersion = /^\d+(?:\.\d+){0,3}$/.test(digits)
    ? [...digits.split('.').map(Number), 0, 0, 0].slice(0, 4).join('.')
    : undefined
  return { framework: name.substring(0, dash).toLowerCase(), platform: platform ? platform.toLowerCase() : undefined, platformVersion }
}

/**
 * How well `framework` stands in for `target`, lower is better, `Infinity` when it cannot: the target itself,
 * then the same kind of framework from the highest version down, then .NET Standard from the highest down.
 */
export function rank(target: string, framework: string): number {
  if (isSameFramework(target, framework)) {
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

/**
 * The framework a project targeting `framework` gets of the part of the docs built for `part`, for the units of the
 * axis value `value`: the nearest of them, as NuGet picks the nearest framework of a package, not of each API. `null`
 * when none can stand in; `undefined` when the part has no unit of that value to tell.
 */
function resolveFramework(framework: string, value: string | undefined, part: Unit[]): string | null | undefined {
  let resolved = resolutions.get(part)
  if (!resolved) {
    resolved = new Map()
    resolutions.set(part, resolved)
  }
  const key = `${value ?? ''}\n${framework.toLowerCase()}`
  if (!resolved.has(key)) {
    const candidates = part.filter(u => u.value === value)
    let best: string | null = candidates.length > 0 ? null : undefined
    let bestRank = Infinity
    for (const unit of candidates) {
      const r = rank(framework, unit.framework)
      if (r < bestRank) {
        best = unit.framework
        bestRank = r
      }
    }
    resolved.set(key, best)
  }
  return resolved.get(key)
}

// The table of contents asks again for every entry of a part, on every click and keystroke in its filter.
const resolutions = new WeakMap<Unit[], Map<string, string | null | undefined>>()

/**
 * Whether a unit stands in for the framework picked: it is the framework the API's part of the docs resolves the pick
 * to, so that an API the build of that framework lacks, such as one only the .NET Standard 2.0 build of a project for
 * .NET 8 and .NET Standard 2.0 has, does not exist on .NET 8; any that can stand in where the part is not known.
 */
function standsIn(framework: string, unit: Unit, part: Unit[] | null | undefined): boolean {
  const resolved = part ? resolveFramework(framework, unit.value, part) : undefined
  return resolved === undefined ? rank(framework, unit.framework) < Infinity : resolved !== null && isSameFramework(resolved, unit.framework)
}

/**
 * Whether a unit is in the selection; `part` is what the API's part of the docs is built for, that of the selection by
 * default, `null` when it is not known.
 */
export function matches(unit: Unit, selection: Selection, part: Unit[] | null | undefined = selection.part): boolean {
  // a value picked for the axis of one part of the docs says nothing about a part without that axis, or with another
  const valueApplies = !!selection.value && unit.value !== undefined && (!selection.axisValues || selection.axisValues.includes(unit.value))
  return (!valueApplies || unit.value === selection.value) &&
    (!selection.framework || standsIn(selection.framework, unit, part)) &&
    (!selection.within || selection.within.some(w =>
      (w.value === undefined || unit.value === undefined || w.value === unit.value) && isSameFramework(w.framework, unit.framework)))
}

export function isAvailable(units: Unit[], selection: Selection, part: Unit[] | null | undefined = selection.part): boolean {
  return units.some(u => matches(u, selection, part))
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

/**
 * The selection a group that `chooseGroups` keeps beside the one in place is shown for: with one dimension picked,
 * the other one from the unit of the group that stands in best for it, e.g. Driver 2 on .NET 8 for .NET 8. Units
 * that stand in equally well, such as Driver 1 on .NET Standard 2.0 and on .NET Framework 4.6.2 for Driver 1, are
 * all kept: the selection is limited to them.
 */
export function narrowSelection(units: Unit[], selection: Selection): Selection {
  const ranked = units.filter(u => matches(u, selection)).map(unit => ({ unit, rank: selection.framework ? rank(selection.framework, unit.framework) : 0 }))
  if (ranked.length === 0) {
    return selection
  }
  const bestRank = Math.min(...ranked.map(r => r.rank))
  const best = ranked.filter(r => r.rank === bestRank).map(r => r.unit)
  return best.length === 1
    ? { framework: selection.framework || best[0].framework, value: selection.value || best[0].value, ...(selection.part ? { part: selection.part } : {}) }
    : { ...selection, within: best }
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
export type TocEntry = { name?: string, href?: string, appliesTo?: AppliesToGroup[], axis?: Axis, type?: string, items?: TocEntry[] }

/** A table of contents as `toc.json` has it. */
export type Toc = { items?: TocEntry[], axis?: Axis, frameworkNames?: Record<string, string> }

/**
 * What a reader can pick on a page: the frameworks of its table of contents, and the axis of its part of the docs;
 * `axes` are those of every part, whose entries the table of contents filters by the value picked for each; `part` what
 * the part the page is in is built for.
 */
export type Choices = { frameworks: string[], axis?: Axis, key?: string, axes?: Axis[], part?: Unit[] }

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
  if (selectionCache) {
    const byFrameworks = selectionCache.get(axis) ?? selectionCache.set(axis, new Map()).get(axis)
    if (!byFrameworks.has(frameworks)) {
      byFrameworks.set(frameworks, readSelection(axis, frameworks))
    }
    return { ...byFrameworks.get(frameworks) }
  }
  return readSelection(axis, frameworks)
}

// The selection by axis and frameworks while the table of contents renders, which asks for that of each entry with an
// axis, and of the entries below each one, on every click and keystroke in its filter.
let selectionCache: Map<Axis | undefined, Map<string[] | undefined, Selection>> | undefined

/** Runs `render` with the selection read once for each axis, as nothing changes it while the table of contents renders. */
export function withSelectionCache<T>(render: () => T): T {
  if (selectionCache) {
    return render()
  }
  selectionCache = new Map()
  try {
    return render()
  } finally {
    selectionCache = undefined
  }
}

// What the reader picked where `localStorage` cannot keep it, for the page: where it is null, such as in a browser set
// to keep no site data, or refuses to, such as when it is full. `null` for "All".
const picks = new Map<string, string | null>()

function keptPick(key: string): string | null {
  return picks.has(key) ? picks.get(key) : (localStorage?.getItem(storageKey(key)) ?? null)
}

function keepPick(key: string, value: string | undefined) {
  if (localStorage) {
    try {
      if (value) {
        localStorage.setItem(storageKey(key), value)
      } else {
        localStorage.removeItem(storageKey(key))
      }
      picks.delete(key)
      return
    } catch {
      // kept for the page below, so that the pick still shows
    }
  }
  if (value) {
    picks.set(key, value)
  } else if (localStorage) {
    // "All", over what storage still has
    picks.set(key, null)
  } else {
    picks.delete(key)
  }
}

function readSelection(axis?: Axis, frameworks?: string[]): Selection {
  const query = new URLSearchParams(window.location.search)
  const read = (key: string, valid: (value: string) => string | undefined) =>
    [query.get(key), keptPick(key)].map(value => value ? valid(value) : undefined).find(value => value)
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
    keepPick('framework', selection.framework)
  }
  const axes = [choices.axis, ...(choices.axes || [])].filter(a => a)
  for (const key of new Set(axes.map(axisKey))) {
    const value = query.get(key)
    if (value && axes.some(a => axisKey(a) === key && a.values.some(v => v.id === value))) {
      keepPick(key, value)
    }
  }
}

/** Whether the page was opened by following a link or entering its address, rather than reloaded or gone back to. */
function isFollowedLink(): boolean {
  const entry = window.performance?.getEntriesByType?.('navigation')?.[0] as PerformanceNavigationTiming | undefined
  return !entry || (entry.type !== 'reload' && entry.type !== 'back_forward')
}

/**
 * Keeps what the reader picked in a dropdown, `undefined` for "All", and shows the page for it; where `localStorage`
 * is null, for this page only.
 */
export function select(key: string, value: string | undefined) {
  keepPick(key, value)
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

type TocSummary = { frameworks: string[], axes: Axis[], part: Unit[] }

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
    // The entries outside any API that are not one, such as the entry of each part in a table of contents that includes
    // several, and the root, are the roots of parts; the groups within an API, such as the categories of the members of
    // a type, are not. A part is built for the units of its own entries, not of the parts in it.
    const seen = new Map<Unit[], Set<string>>()
    const visit = (items: TocEntry[], part: Unit[], inApi: boolean) => items?.forEach(item => {
      unitsOf(item.appliesTo).forEach(u => {
        frameworks.push(u.framework)
        const key = `${u.value ?? ''}:${u.framework.toLowerCase()}`
        if (!seen.get(part).has(key)) {
          seen.get(part).add(key)
          part.push(u)
        }
      })
      if (item.axis) {
        add(item.axis)
      }
      const api = isApiEntry(item)
      entryParts.set(item, part)
      const root = !inApi && !api && item.items?.length > 0
      visit(item.items, root ? newPart() : part, inApi || api)
    })
    const newPart = () => {
      const part: Unit[] = []
      seen.set(part, new Set())
      return part
    }
    const part = newPart()
    visit(toc.items, part, false)
    summaries.set(toc, { frameworks: sortFrameworks(frameworks), axes, part })
  }
  return summaries.get(toc)
}

/**
 * What a reader can pick on a page: the frameworks of its table of contents, and the axis of the part of the docs
 * the page is in, given by the entries from the root down to the page; on another page, such as an article, the axis when
 * there is one only. An API page of a part without an axis has none, even when another part has one.
 */
export function getChoices(toc: Toc, path: TocEntry[] = []): Choices {
  const { frameworks, axes, part } = summarize(toc)
  const axis = [...path].reverse().find(entry => entry.axis)?.axis || toc.axis ||
    (axes.length === 1 && !path.some(entry => hasData(entry)) ? axes[0] : undefined)
  const page = path[path.length - 1]
  return { frameworks, axis, key: axis ? axisKey(axis) : undefined, axes, part: (page && entryParts.get(page)) || part }
}

/** Whether an entry is an API, which `docfx metadata` writes with its type, and with `appliesTo` for its frameworks. */
function isApiEntry(entry: TocEntry): boolean {
  return hasData(entry) || typeof entry.type === 'string'
}

// What the part of the docs each entry of a table of contents that `summarize` read is in is built for.
const entryParts = new WeakMap<TocEntry, Unit[]>()

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
  return !hasData(entry) || !(selection.framework || selection.value) || isAvailable(unitsOf(entry.appliesTo), selection, entryParts.get(entry))
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
 * The entries with a link that "Next article" goes through, in order: with the picker, those the table of contents
 * shows for the selection, the page being read and its parents, `path`, kept; without it, every one, read as without
 * framework data, so that a table of contents that uses `appliesTo` or `axis` for something else works as before.
 */
export function articleEntries(toc: Toc, path: TocEntry[]): TocEntry[] {
  if (!isPickerEnabled()) {
    const flatten = (items: TocEntry[]): TocEntry[] => (items || []).flatMap(item => [...(item.href ? [item] : []), ...flatten(item.items)])
    return flatten(toc.items)
  }
  const { frameworks } = getChoices(toc, path)
  return shownEntries(toc.items, getSelection(toc.axis, frameworks), frameworks, entry => path.includes(entry))
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

/**
 * Whether the navbar shows an entry of its table of contents, `parent` the entry of the dropdown it is in: what the
 * selection leaves out goes, as in the table of contents, where the navbar's is that of an API, such as with
 * `_disableToc`; the page being read and its dropdown, `active`, stay. Any other names no framework, so it stays whole.
 */
export function isNavEntryShown(toc: Toc, entry: TocEntry | undefined, parent: TocEntry | undefined, active: boolean): boolean {
  if (!isPickerEnabled() || active || !entry) {
    return true
  }
  const { frameworks } = getChoices(toc)
  if (frameworks.length === 0) {
    return true
  }
  const root = getSelection(toc.axis, frameworks)
  return entryDisplay(entry, parent ? entrySelection(parent, root, frameworks) : root, frameworks, () => false) !== 'none'
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
  // What the page's own APIs are documented for stands in for what its part of the docs is built for until then.
  const early: Selection = isPickerEnabled() ? { ...getSelection(), part: pageUnits() } : {}
  if (early.framework && !early.part.some(u => u.framework.toLowerCase() === early.framework.toLowerCase())) {
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
    applySelection({ ...getSelection(choices.axis, choices.frameworks), part: choices.part }, choices.axis)
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
  // link shows what it names also where `localStorage` is null (a browser where reading it throws stops the scripts).
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

/**
 * What the APIs of the page are documented for: not the extension methods it lists, which can be declared in another
 * part of the docs, built for frameworks this part is not.
 */
function pageUnits(): Unit[] {
  return Array.from(document.querySelectorAll<HTMLElement>('article [data-applies-to]'))
    .filter(e => !e.closest('dl.extensionMethods'))
    .flatMap(e => parseUnits(e.dataset.appliesTo))
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
  const blocks = blocksOf(container)
  const base = withoutCopySuffix(id, blocks.indexOf(target.closest('.api-content')))
  for (const [index, block] of blocks.entries()) {
    // the content shown in place, rather than a copy an entry of "In other frameworks" shows whole
    if (!block.hidden && block.parentElement === container) {
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
  // a selection limited to some units names each of their values and frameworks the pick leaves open
  const within = selection.within || []
  const values = selection.value ? [selection.value] : [...new Set(within.map(u => u.value).filter(v => v))]
  const frameworks = selection.framework ? [selection.framework] : [...new Set(within.map(u => u.framework))]
  return [
    ...values.map(value => axis?.values.find(v => v.id === value)?.label),
    ...frameworks.map(framework => frameworkLabel(framework, frameworkNames()))
  ].filter(s => s).join(', ')
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
  putBackWholeCopies()

  // the variants under "In other frameworks" that follow the content of an API, and those of them still shown
  const listed = new Set<Element>()
  const kept = new Set<Element>()
  const whole: { entry: HTMLElement, blocks: { block: HTMLElement, previous: HTMLElement }[] }[] = []
  // A copy an entry shows whole is the API as it is in the frameworks of its variant, so what the copy holds that
  // differs again, such as the values of an enum, is shown for the selection narrowed to the copy.
  const copySelections = new Map<Element, Selection>()
  const selectionOf = (e: Element): Selection => {
    for (let block = e.closest('.api-content'); block; block = block.parentElement?.closest('.api-content')) {
      if (copySelections.has(block)) {
        return copySelections.get(block)
      }
    }
    return selection
  }
  const labelOf = (e: Element) => {
    const own = selectionOf(e)
    return own === selection ? label : selectionLabel(own, axis)
  }
  // a container comes before the containers in it on the page
  document.querySelectorAll('.api-contents').forEach(container => {
    const own = selectionOf(container)
    const blocks = blocksOf(container)
    const shown = any ? chooseGroups(blocks.map(b => parseUnits(b.dataset.appliesTo)), own) : blocks.map((_, i) => i)
    const inPlace = shown.length > 0 ? shown[0] : 0
    blocks.forEach((block, i) => { block.hidden = i !== inPlace })
    // the copy of each variant follows the API's own content, in the order "In other frameworks" lists them
    const variants = variantsAfter(container) as HTMLElement[]
    variants.forEach(v => listed.add(v))
    for (const i of shown.filter(i => i > 0 && i !== inPlace && variants[i - 1])) {
      kept.add(variants[i - 1])
      // An entry lists what its variant differs in from the API's own content, which a variant shown in place hides:
      // the entry shows its variant whole instead.
      if (inPlace !== 0) {
        const { before, after } = companionsOf(container)
        const copyOf = (c: Element) => {
          const b = blocksOf(c)
          // a companion has a copy for each variant, as the API's own content has
          return b[i]?.dataset.appliesTo === blocks[i].dataset.appliesTo ? [{ block: b[i], previous: b[i - 1] }] : []
        }
        whole.push({
          entry: variants[i - 1],
          blocks: [...before.flatMap(copyOf), { block: blocks[i], previous: blocks[i - 1] }, ...after.flatMap(copyOf)]
        })
        copySelections.set(blocks[i], narrowSelection(parseUnits(blocks[i].dataset.appliesTo), own))
      }
    }
  })
  document.querySelectorAll<HTMLElement>('.api-variant').forEach(e => { e.hidden = listed.has(e) ? !kept.has(e) : any })
  whole.forEach(showWholeCopy)
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
    if (isAvailable(parseUnits(heading.dataset.appliesTo), selectionOf(heading))) {
      return
    }
    if (print) {
      memberElements(heading).forEach(leaveOut)
      return
    }
    heading.appendChild(unavailableBadge(labelOf(heading), false))
    for (const e of memberElements(heading)) {
      e.classList.add('applies-to-unavailable')
    }
  })

  // In print, the anchor of an overload group, which each of its members follows, goes when all of them do, so that a
  // link to the group is text, as to a member left out.
  if (print) {
    const anchors = document.querySelectorAll<HTMLElement>('article a[data-overload][id]')
    for (const id of new Set(Array.from(anchors, a => a.id))) {
      const group = Array.from(anchors).filter(a => a.id === id)
      if (group.every(a => a.nextElementSibling?.hasAttribute('data-applies-to-left-out'))) {
        group.forEach(leaveOut)
      }
    }
  }

  // The entries of a list are marked as members are, not only dimmed, which a reader who cannot tell the colours
  // apart, or does not see them, would miss.
  // the types a namespace page lists, and the members of a type with a page each
  document.querySelectorAll<HTMLElement>('article dl.jumplist[data-applies-to]').forEach(entry => {
    if (!isAvailable(parseUnits(entry.dataset.appliesTo), selectionOf(entry))) {
      if (print) {
        leaveOut(entry)
      } else {
        entry.classList.add('applies-to-unavailable')
        entry.querySelector('dt')?.appendChild(unavailableBadge(labelOf(entry), true))
      }
    }
  })

  // the classes derived, the interfaces implemented, and the members inherited, in only some frameworks of a type,
  // as a base type differs, and
  // extension methods declared in only some, which can be in another part of the docs, with another axis or none
  document.querySelectorAll<HTMLElement>('article dl.typelist').forEach(list => {
    // an extension method can be declared in another part of the docs, built for frameworks the page does not know:
    // any that can stand in does
    const extensionMethods = list.classList.contains('extensionMethods')
    const entries = Array.from(list.querySelectorAll<HTMLElement>('dd div'))
    entries.filter(entry => {
      const own = selectionOf(entry)
      return entry.dataset.appliesTo && !isAvailable(unitsOnAxis(parseUnits(entry.dataset.appliesTo), axis), own, extensionMethods ? null : own.part)
    }).forEach(entry => {
      if (print) {
        leaveOut(entry)
      } else {
        entry.classList.add('applies-to-unavailable')
        entry.appendChild(unavailableBadge(labelOf(entry), true))
      }
    })
    if (print && entries.length > 0 && entries.every(entry => entry.hidden)) {
      leaveOut(list)
    }
  })

  // In print, a section whose members are all left out goes too: "Events" on a type page, or "Interfaces" on a
  // namespace page, where the headings of such groups are `h3`s without frameworks of their own. Only on API pages,
  // and by the members that have frameworks: a hidden tab of an article has terms of its own. Members left out, not
  // hidden: the values of an enum in its own content, when a copy is shown in its place, are printed in the copy.
  if (print && document.querySelector('article h1[data-applies-to]')) {
    const member = 'h3[data-applies-to], dt[data-applies-to], dl.jumplist[data-applies-to]'
    // the headings of a member's own markdown, such as its example, are none of the page's sections
    const ofMembers = new Set(Array.from(document.querySelectorAll('article h3[data-uid]')).flatMap(h => memberElements(h).slice(1)))
    const inMember = (e: Element) => {
      for (let a = e; a; a = a.parentElement) {
        if (ofMembers.has(a)) {
          return true
        }
      }
      return false
    }
    document.querySelectorAll<HTMLElement>('article h2, article h3:not([data-applies-to])').forEach(section => {
      if (inMember(section)) {
        return
      }
      const ends = section.tagName === 'H2' ? /^H[12]$/ : /^H[1-3]$/
      const following: HTMLElement[] = []
      for (let e = section.nextElementSibling; e && !(ends.test(e.tagName) && !inMember(e)); e = e.nextElementSibling) {
        following.push(e as HTMLElement)
      }
      const members = following.flatMap(e => e.matches(member) ? [e] : Array.from(e.querySelectorAll<HTMLElement>(member)))
      if (members.length > 0 && members.every(m => m.closest('[data-applies-to-left-out]'))) {
        leaveOut(section)
        following.forEach(leaveOut)
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
  // a copy an entry shows whole repeats the API's own headings, which the outline has once
  return !!element.closest('.api-content[hidden], .api-variant[hidden], .api-variants-heading[hidden], .api-variant .api-content')
}

/**
 * In print, leaves out of the page what does not exist in the selection, marked for `docfx pdf`, which makes the
 * links to it text, as to a page the PDF leaves out.
 */
function leaveOut(element: Element) {
  (element as HTMLElement).hidden = true
  element.setAttribute('data-applies-to-left-out', '')
}

/** The badge of something that does not exist in the selection; smaller in a list than in a heading. */
function unavailableBadge(label: string, inList: boolean): HTMLElement {
  const badge = document.createElement('span')
  badge.className = `applies-to-unavailable-badge badge text-bg-secondary fw-normal ms-2${inList ? '' : ' fs-6'}`
  badge.textContent = loc('appliesToUnavailable', { selection: label })
  return badge
}

/**
 * The content of an API, then the hidden copy of each variant, as the page has them: a copy that an entry of "In
 * other frameworks" shows whole is in that entry, until it is put back.
 */
const blocksByContainer = new WeakMap<Element, HTMLElement[]>()

function blocksOf(container: Element): HTMLElement[] {
  if (!blocksByContainer.has(container)) {
    blocksByContainer.set(container, Array.from(container.children).filter(e => e.classList.contains('api-content')) as HTMLElement[])
  }
  return blocksByContainer.get(container)
}

// The copies the entries of "In other frameworks" show whole, and what of the entry each hides, in the order the
// copies are on the page.
let wholeCopies: { block: HTMLElement, previous: HTMLElement, hidden: HTMLElement[] }[] = []

/**
 * Shows the copy of a variant in its entry of "In other frameworks", below its label, in place of what it lists: the
 * copies of its companions with it, in the order of the page.
 */
function showWholeCopy({ entry, blocks }: { entry: HTMLElement, blocks: { block: HTMLElement, previous: HTMLElement }[] }) {
  const children = Array.from(entry.children) as HTMLElement[]
  const label = children.find(e => e.classList.contains('api-variant-label'))
  const hidden = children.filter(e => e !== label && !e.hidden)
  hidden.forEach(e => { e.hidden = true })
  let last: HTMLElement | undefined = label
  blocks.forEach(({ block, previous }, i) => {
    if (last) {
      last.after(block)
    } else {
      entry.prepend(block)
    }
    last = block
    block.hidden = false
    wholeCopies.push({ block, previous, hidden: i === 0 ? hidden : [] })
  })
}

/**
 * The containers that hold more of the API whose content `container` holds, with a copy for each variant and no "In
 * other frameworks" of their own: the declaration of an enum value, in the term before the description that the
 * variants follow, and the see-also links of a type page, after the content of the type. An entry that shows the copy
 * of a variant whole shows theirs with it.
 */
function companionsOf(container: Element): { before: Element[], after: Element[] } {
  const contentsIn = (e: Element | null | undefined) => Array.from(e?.children || []).filter(c => c.classList.contains('api-contents'))
  const term = container.parentElement?.tagName === 'DD' ? container.parentElement.previousElementSibling : null
  const before = term?.tagName === 'DT' ? contentsIn(term) : []
  // the content of a type follows its facts, and the banner of a selection that lacks it
  let previous = container.previousElementSibling
  while (previous?.classList.contains('applies-to-banner')) {
    previous = previous.previousElementSibling
  }
  const after = previous?.classList.contains('facts') ? contentsIn(container.parentElement).filter(isSeeAlsoContents) : []
  return { before, after }
}

function putBackWholeCopies() {
  // a copy goes back after the one before it, which is back already
  for (const { block, previous, hidden } of wholeCopies) {
    previous.after(block)
    block.hidden = true
    hidden.forEach(e => { e.hidden = false })
  }
  wholeCopies = []
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
 * enum, its term and description. The heading of a member docfx writes has its uid: its example is markdown as it
 * is, whose headings are not the page's, so it ends only at the next member, section, or the see-also links of
 * the page.
 */
export function memberElements(heading: Element): Element[] {
  const elements = [heading]
  const written = heading.hasAttribute('data-uid')
  for (let e = heading.nextElementSibling; e; e = e.nextElementSibling) {
    const ends = heading.tagName === 'DT'
      ? e.tagName !== 'DD'
      : e.hasAttribute('data-overload') || isSeeAlsoContents(e) || (written ? endsMember(e) : /^H[1-3]$/.test(e.tagName))
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

function endsMember(e: Element): boolean {
  return e.tagName === 'H1' ||
    (e.tagName === 'H2' && (e.classList.contains('section') || isSeeAlso(e))) ||
    (e.tagName === 'H3' && e.hasAttribute('data-uid'))
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
    const blocks = blocksOf(container)
    blocks.slice(1).forEach((copy, i) => {
      const suffix = `_variant${i + 1}`
      const renamed = new Map<string, string>()
      copy.querySelectorAll('[id]').forEach(e => {
        if (document.getElementById(e.id) !== e) {
          renamed.set(e.id, e.id + suffix)
          e.id += suffix
        }
      })
      // the tabs of a copy, such as those of its remarks, control the panels of the copy
      copy.querySelectorAll('[aria-controls]').forEach(tab => {
        const panel = renamed.get(tab.getAttribute('aria-controls'))
        if (panel) {
          tab.setAttribute('aria-controls', panel)
          if (tab.getAttribute('href')?.startsWith('#')) {
            tab.setAttribute('href', `#${panel}`)
          }
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
     * Set by `docfx pdf` for a PDF of one framework or axis value: `pdfFramework`, `pdfAxisValue`, the ids of the
     * axis that value is one of, when the table of contents names it, and the paths of the pages of the axes that do
     * not have the value, whose own values may have the same ids. `applied` is set once the page shows it, which
     * `docfx pdf` reads to warn about the pages of a template that does not; `error`, when the page could not.
     */
    docfxAppliesTo?: { framework?: string, value?: string, axisValues?: string[], otherAxisPages?: string[], applied?: boolean, error?: string }
  }
}

/** In a PDF of one framework or axis value, shows the pages for it. */
export function applyPrintSelection() {
  const selection = window.docfxAppliesTo
  if (!selection) {
    return
  }
  try {
    // A copy shown in place of the API's own content has the ids of its headings to itself, as on the site, so that
    // the outline of a PDF with `pdfTocSource: headings` goes to the heading printed.
    distinguishCopies()
    // the value of another axis says nothing about this page
    const otherAxis = !!selection.otherAxisPages?.includes(window.location.pathname)
    applySelection({
      framework: selection.framework || undefined,
      value: otherAxis ? undefined : selection.value || undefined,
      axisValues: otherAxis ? undefined : selection.axisValues || undefined
    }, undefined, true)
    selection.applied = true
  } catch (e) {
    // The page is printed as far as the selection was applied, and `docfx pdf` says why, rather than wait for a page
    // that is never ready.
    console.error(e)
    selection.error = String((e as Error)?.message || e)
  }
}

/** The display name of a framework, as the templates write it in Applies to. */
export function frameworkLabel(framework: string, names?: Record<string, string>): string {
  // the frameworks are written in lower case, a key of `_frameworkNames` as the site writes it
  const key = names && Object.keys(names).find(k => k.toLowerCase() === framework.toLowerCase())
  if (names && key !== undefined) {
    return names[key]
  }
  let match = /^netstandard(\d+\.\d+)$/i.exec(framework)
  if (match) {
    return `.NET Standard ${match[1]}`
  }
  match = /^netcoreapp(\d+\.\d+)$/i.exec(framework)
  if (match) {
    return `.NET Core ${match[1]}`
  }
  match = /^net(\d+)\.(\d+)(?:-([a-z]+)([\d.]*))?$/i.exec(framework)
  if (match) {
    const name = `.NET ${match[1]}${match[2] === '0' ? '' : '.' + match[2]}`
    return match[3] ? `${name} for ${platformLabel(match[3])}${match[4] ? ' ' + match[4] : ''}` : name
  }
  match = /^net(\d)(\d)(\d)?$/i.exec(framework)
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
