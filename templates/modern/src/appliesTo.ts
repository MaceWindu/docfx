// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import { html, TemplateResult } from 'lit-html'
import { loc, meta } from './helper'

/**
 * What an API is documented for: a target framework, and the value of the entry's axis, such as an EF Core
 * version, when it has one. Pages write it `value:framework` in `data-applies-to`, e.g. `ef9:net8.0`.
 */
export type Unit = { value?: string, framework: string }

/** What the reader picked. A missing part is "all". */
export type Selection = { value?: string, framework?: string }

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

function parseMoniker(moniker: string): Moniker {
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
  // a value picked for the axis of one part of the docs says nothing about a part without that axis
  return (!selection.value || unit.value === undefined || unit.value === selection.value) &&
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

/** The axis of a part of the docs, as `docfx metadata` writes it to the root of its table of contents. */
export type Axis = { name: string, values: { id: string, label: string }[], default?: string }

type Choices = { frameworks: string[], axis?: Axis, key?: string }

const changeEvent = 'docfx:appliestochange'

export function isPickerEnabled(): boolean {
  return meta('docfx:appliestopicker') === 'true'
}

/**
 * The key of an axis in storage and in the query string: its name in lower case, e.g. `ef-core`; never
 * `framework`, which is the key of the frameworks.
 */
export function axisKey(axis: Axis): string {
  const key = axis.name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '')
  return key === 'framework' ? 'framework-axis' : key
}

/** Whether a framework can be picked: one docfx can compare, or one the table of contents lists. */
export function isValidFramework(framework: string, frameworks: string[] = []): boolean {
  return !!parseMoniker(framework) || frameworks.includes(framework)
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
 * What the reader picked: from the query string, for shareable links, else what they picked last. A value that
 * is not one of the axis, or a framework that is not one, is left out, so a typo in a link or a choice kept from
 * an earlier version of the site does not hide everything.
 */
export function getSelection(axis?: Axis, frameworks: string[] = []): Selection {
  const query = new URLSearchParams(window.location.search)
  const read = (key: string, valid: (value: string) => boolean) =>
    [query.get(key), localStorage?.getItem(storageKey(key))].find(value => value && valid(value)) || undefined
  return {
    framework: read('framework', f => isValidFramework(f, frameworks)),
    value: axis ? read(axisKey(axis), v => axis.values.some(a => a.id === v)) : undefined
  }
}

/** Keeps the choice a link made, so that it carries on to the pages the reader goes to next. */
function keepLinkSelection(choices: Choices) {
  const query = new URLSearchParams(window.location.search)
  const selection = getSelection(choices.axis, choices.frameworks)
  if (query.get('framework') && query.get('framework') === selection.framework) {
    localStorage?.setItem(storageKey('framework'), selection.framework)
  }
  if (choices.key && query.get(choices.key) && query.get(choices.key) === selection.value) {
    localStorage?.setItem(storageKey(choices.key), selection.value)
  }
}

function select(key: string, value: string | undefined) {
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

/** The frameworks and axis of the current table of contents: what a reader can pick on this page. */
export function getChoices(toc: { items?: { appliesTo?: AppliesToGroup[], items?: unknown[] }[], axis?: Axis }): Choices {
  const frameworks: string[] = []
  const visit = (items: { appliesTo?: AppliesToGroup[], items?: unknown[] }[]) => items?.forEach(item => {
    unitsOf(item.appliesTo).forEach(u => frameworks.push(u.framework))
    visit(item.items as { appliesTo?: AppliesToGroup[], items?: unknown[] }[])
  })
  visit(toc.items)
  return { frameworks: sortFrameworks(frameworks), axis: toc.axis, key: toc.axis ? axisKey(toc.axis) : undefined }
}

const tocs = new Map<string, Promise<unknown>>()

/** The table of contents at `url`, fetched once for the table of contents, the navbar and the picker. */
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function fetchToc(url: URL): Promise<any> {
  if (!tocs.has(url.href)) {
    tocs.set(url.href, fetch(url).then(res => res.json()))
  }
  return tocs.get(url.href)
}

/** The choices of the current table of contents, none when the picker is off. */
export async function loadChoices(): Promise<Choices> {
  const tocrel = meta('docfx:tocrel')
  if (!isPickerEnabled() || !tocrel) {
    return { frameworks: [] }
  }
  return getChoices(await fetchToc(new URL(tocrel.replace(/.html$/gi, '.json'), window.location.href)))
}

/**
 * Applies the selection to the page now and whenever the reader picks another, then calls `onApplied`, which
 * updates what depends on the content shown, such as the headings of "In this article".
 */
export async function initAppliesTo(onApplied: () => void = () => { }) {
  // The framework can be applied before the table of contents is loaded, so that the page does not show all
  // frameworks first; the value of an axis needs the axis, which the table of contents names.
  const early = isPickerEnabled() ? getSelection() : {}
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
  onSelectionChange(apply)
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
  const dropdown = (key: string, title: string, current: string, selected: string | undefined, items: { id: string, label: string }[]) => html`
    <div class='dropdown applies-to-picker'>
      <button type='button' title='${title}' class='btn border-0 dropdown-toggle' data-bs-toggle='dropdown' aria-expanded='false'>${current}</button>
      <ul class='dropdown-menu dropdown-menu-end'>
        ${items.map(i => html`<li><a class='dropdown-item ${i.id === selected ? 'active' : ''}' aria-current=${i.id === selected ? 'true' : 'false'} href='#'
          @click=${(e: Event) => { e.preventDefault(); select(key, i.id) }}>${i.label}</a></li>`)}
      </ul>
    </div>`

  const axis = choices.axis
  const axisDropdown = axis
    ? dropdown(choices.key, axis.name,
      // a value's label already names the axis ("EF Core 3.1"), "All" does not
      axis.values.find(v => v.id === selection.value)?.label || `${axis.name}: ${loc('appliesToAll')}`,
      selection.value,
      [{ id: undefined, label: loc('appliesToAll') }, ...axis.values])
    : null
  const frameworkDropdown = dropdown('framework', loc('appliesToFramework'),
    selection.framework ? frameworkLabel(selection.framework, names) : loc('appliesToAllFrameworks'),
    selection.framework,
    [{ id: undefined, label: loc('appliesToAllFrameworks') }, ...choices.frameworks.map(f => ({ id: f, label: frameworkLabel(f, names) }))])
  return html`${axisDropdown}${frameworkDropdown}`
}

function frameworkNames(): Record<string, string> {
  try {
    return JSON.parse(meta('docfx:frameworknames') || '{}')
  } catch {
    return {}
  }
}

/** Describes a selection in a banner or a badge, e.g. `EF Core 3.1, .NET Framework 4.6.2`. */
function selectionLabel(selection: Selection, axis?: Axis): string {
  const value = axis?.values.find(v => v.id === selection.value)?.label
  const framework = selection.framework ? frameworkLabel(selection.framework, frameworkNames()) : undefined
  return [value, framework].filter(s => s).join(', ')
}

/**
 * Shows the page for the selection: the content of each API for the framework that stands in best, without the
 * other frameworks' differences; members that do not exist in the selection dimmed; a banner when the API itself
 * does not exist in it.
 */
export function applySelection(selection: Selection, axis?: Axis) {
  const any = !!(selection.framework || selection.value)
  const label = selectionLabel(selection, axis)

  document.querySelectorAll('.api-contents').forEach(container => {
    const blocks = Array.from(container.children).filter(e => e.classList.contains('api-content')) as HTMLElement[]
    const chosen = any ? chooseGroup(blocks.map(b => parseUnits(b.dataset.appliesTo)), selection) : 0
    blocks.forEach((block, i) => { block.hidden = i !== Math.max(chosen, 0) })
  })
  document.querySelectorAll<HTMLElement>('.api-variants-heading, .api-variant').forEach(e => { e.hidden = any })

  document.querySelectorAll('.applies-to-unavailable-badge, .applies-to-banner').forEach(e => e.remove())
  document.querySelectorAll('.applies-to-unavailable').forEach(e => e.classList.remove('applies-to-unavailable'))
  if (!any) {
    return
  }

  // members, and the values of an enum, which are a list of their own
  document.querySelectorAll<HTMLElement>('article h3[data-applies-to], article dt[data-applies-to]').forEach(heading => {
    if (isAvailable(parseUnits(heading.dataset.appliesTo), selection)) {
      return
    }
    const badge = document.createElement('span')
    badge.className = 'applies-to-unavailable-badge badge text-bg-secondary fs-6 fw-normal ms-2'
    badge.textContent = loc('appliesToUnavailable', { selection: label })
    heading.appendChild(badge)
    for (const e of memberElements(heading)) {
      e.classList.add('applies-to-unavailable')
    }
  })

  const title = document.querySelector<HTMLElement>('article h1[data-applies-to]')
  if (title && !isAvailable(parseUnits(title.dataset.appliesTo), selection)) {
    const banner = document.createElement('div')
    banner.className = 'applies-to-banner alert alert-warning'
    banner.textContent = loc('appliesToUnavailable', { selection: label })
    title.parentElement.querySelector('.facts')?.after(banner)
  }
}

/**
 * The elements of a member: its heading and what follows up to the next heading or member, or for the value of an
 * enum, its term and description.
 */
export function memberElements(heading: Element): Element[] {
  const elements = [heading]
  for (let e = heading.nextElementSibling; e; e = e.nextElementSibling) {
    const ends = heading.tagName === 'DT' ? e.tagName !== 'DD' : /^H[1-3]$/.test(e.tagName) || e.hasAttribute('data-overload')
    if (ends) {
      break
    }
    elements.push(e)
  }
  return elements
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
  match = /^net(\d+)\.(\d+)(-.+)?$/.exec(framework)
  if (match) {
    return `.NET ${match[1]}${match[2] === '0' ? '' : '.' + match[2]}${match[3] || ''}`
  }
  match = /^net(\d)(\d)(\d)?$/.exec(framework)
  if (match) {
    return `.NET Framework ${match[1]}.${match[2]}${match[3] ? '.' + match[3] : ''}`
  }
  return framework
}
