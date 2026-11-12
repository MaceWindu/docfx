// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
  return (!selection.value || unit.value === selection.value) &&
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
  const order = (f: string) => {
    const m = parseMoniker(f)
    return m ? [m.family === Family.Core ? 0 : m.family === Family.Standard ? 1 : 2, m.version] as const : [3, []] as const
  }
  return [...new Set(frameworks)].sort((a, b) => {
    const [fa, va] = order(a)
    const [fb, vb] = order(b)
    return fa - fb || compareVersions(vb, va) || a.localeCompare(b)
  })
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
