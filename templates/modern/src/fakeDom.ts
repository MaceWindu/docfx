// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

/**
 * Just enough of a browser for the tests of what a page does for a framework or axis value: elements parsed from
 * simple markup, selectors of tags, ids, classes and attributes with descendant combinators, the location, local
 * storage and history. Not part of the bundle.
 */

type Compound = { tag?: string, id?: string, classes: string[], attributes: { name: string, value?: string }[], not: Compound[][] }

const voidTags = new Set(['meta', 'br', 'img', 'input', 'hr', 'link'])

export class FakeElement {
  readonly tagName: string
  readonly attributes = new Map<string, string>()
  childNodes: (FakeElement | string)[] = []
  parentElement: FakeElement | null = null
  scrolledIntoView = false

  constructor(tag: string) {
    this.tagName = tag.toUpperCase()
  }

  get children(): FakeElement[] {
    return this.childNodes.filter((n): n is FakeElement => n instanceof FakeElement)
  }

  get id(): string {
    return this.getAttribute('id') || ''
  }

  get className(): string {
    return this.getAttribute('class') || ''
  }

  set className(value: string) {
    this.setAttribute('class', value)
  }

  get classList() {
    const classes = () => this.className.split(/\s+/).filter(c => c)
    return {
      contains: (c: string) => classes().includes(c),
      add: (...added: string[]) => { this.className = [...new Set([...classes(), ...added])].join(' ') },
      remove: (...removed: string[]) => { this.className = classes().filter(c => !removed.includes(c)).join(' ') }
    }
  }

  get dataset(): Record<string, string | undefined> {
    return new Proxy({}, {
      get: (_, key: string) => this.getAttribute('data-' + key.replace(/[A-Z]/g, c => '-' + c.toLowerCase())) ?? undefined
    })
  }

  get hidden(): boolean {
    return this.hasAttribute('hidden')
  }

  set hidden(value: boolean) {
    if (value) {
      this.setAttribute('hidden', '')
    } else {
      this.removeAttribute('hidden')
    }
  }

  get content(): string | undefined {
    return this.getAttribute('content') ?? undefined
  }

  get textContent(): string {
    return this.childNodes.map(n => typeof n === 'string' ? n : n.textContent).join('')
  }

  set textContent(text: string) {
    this.childNodes.forEach(n => { if (n instanceof FakeElement) n.parentElement = null })
    this.childNodes = [text]
  }

  get nextElementSibling(): FakeElement | null {
    const siblings = this.parentElement?.children || []
    return siblings[siblings.indexOf(this) + 1] || null
  }

  getAttribute(name: string): string | null {
    return this.attributes.has(name) ? this.attributes.get(name) : null
  }

  setAttribute(name: string, value: string) {
    this.attributes.set(name, value)
  }

  removeAttribute(name: string) {
    this.attributes.delete(name)
  }

  hasAttribute(name: string): boolean {
    return this.attributes.has(name)
  }

  appendChild(child: FakeElement): FakeElement {
    child.remove()
    child.parentElement = this
    this.childNodes.push(child)
    return child
  }

  after(node: FakeElement) {
    node.remove()
    const parent = this.parentElement
    parent.childNodes.splice(parent.childNodes.indexOf(this) + 1, 0, node)
    node.parentElement = parent
  }

  remove() {
    if (this.parentElement) {
      this.parentElement.childNodes.splice(this.parentElement.childNodes.indexOf(this), 1)
      this.parentElement = null
    }
  }

  matches(selector: string): boolean {
    return parseSelectorList(selector).some(complex => matchesComplex(this, complex))
  }

  closest(selector: string): FakeElement | null {
    return this.matches(selector) ? this : (this.parentElement?.closest(selector) ?? null)
  }

  querySelectorAll(selector: string): FakeElement[] {
    const list = parseSelectorList(selector)
    return descendants(this).filter(e => list.some(complex => matchesComplex(e, complex)))
  }

  querySelector(selector: string): FakeElement | null {
    return this.querySelectorAll(selector)[0] || null
  }

  scrollIntoView() {
    this.scrolledIntoView = true
  }
}

function descendants(element: FakeElement): FakeElement[] {
  return element.children.flatMap(child => [child, ...descendants(child)])
}

function split(text: string, separator: RegExp): string[] {
  const parts: string[] = []
  let depth = 0
  let quoted = false
  let current = ''
  for (const c of text) {
    if (c === '"') quoted = !quoted
    if (!quoted && (c === '(' || c === '[')) depth++
    if (!quoted && (c === ')' || c === ']')) depth--
    if (!quoted && depth === 0 && separator.test(c)) {
      parts.push(current)
      current = ''
    } else {
      current += c
    }
  }
  parts.push(current)
  return parts.map(p => p.trim()).filter(p => p)
}

function parseSelectorList(selector: string): Compound[][] {
  return split(selector, /,/).map(complex => split(complex, /\s/).map(parseCompound))
}

function parseCompound(text: string): Compound {
  const compound: Compound = { classes: [], attributes: [], not: [] }
  const pattern = /^[a-z0-9*]+|#[\w-]+|\.[\w-]+|\[([\w:-]+)(?:="([^"]*)")?\]|:not\(([^)]*)\)/gi
  for (const match of text.matchAll(pattern)) {
    const token = match[0]
    if (token.startsWith('#')) compound.id = token.substring(1)
    else if (token.startsWith('.')) compound.classes.push(token.substring(1))
    else if (token.startsWith('[')) compound.attributes.push({ name: match[1], value: match[2] })
    else if (token.startsWith(':not(')) compound.not.push(...parseSelectorList(match[3]))
    else if (token !== '*') compound.tag = token.toUpperCase()
  }
  return compound
}

function matchesCompound(e: FakeElement, c: Compound): boolean {
  return (!c.tag || e.tagName === c.tag) &&
    (!c.id || e.id === c.id) &&
    c.classes.every(name => e.classList.contains(name)) &&
    c.attributes.every(a => e.hasAttribute(a.name) && (a.value === undefined || e.getAttribute(a.name) === a.value)) &&
    !c.not.some(complex => matchesComplex(e, complex))
}

function matchesComplex(e: FakeElement, complex: Compound[]): boolean {
  if (!matchesCompound(e, complex[complex.length - 1])) {
    return false
  }
  let ancestor = e.parentElement
  for (let i = complex.length - 2; i >= 0; i--) {
    while (ancestor && !matchesCompound(ancestor, complex[i])) {
      ancestor = ancestor.parentElement
    }
    if (!ancestor) {
      return false
    }
    ancestor = ancestor.parentElement
  }
  return true
}

/** Parses well-formed markup with double-quoted attributes, as the tests write it. */
export function parseHtml(markup: string): FakeElement {
  const root = new FakeElement('html')
  let current = root
  for (const match of markup.matchAll(/<(\/?)([a-zA-Z0-9]+)((?:\s+[^\s=>/]+(?:="[^"]*")?)*)\s*(\/?)>|[^<]+/g)) {
    const [token, closing, tag, attributes, selfClosing] = match
    if (!tag) {
      if (token.trim()) current.childNodes.push(token)
      continue
    }
    if (closing) {
      current = current.parentElement || root
      continue
    }
    const element = new FakeElement(tag)
    for (const [, name, value] of attributes.matchAll(/([^\s=]+)(?:="([^"]*)")?/g)) {
      element.setAttribute(name, value ?? '')
    }
    current.appendChild(element)
    if (!selfClosing && !voidTags.has(tag.toLowerCase())) {
      current = element
    }
  }
  return root
}

/** The browser of a test: `document` of the markup at `url`, empty local storage, and history. */
export function installDom(markup: string, url = 'https://example.org/docs/api/Lib.Shared.html') {
  const root = parseHtml(markup)
  const listeners = new Map<string, (() => void)[]>()
  const items = new Map<string, string>()
  const location = { url: new URL(url) }

  const document = {
    documentElement: root,
    querySelectorAll: (selector: string) => root.querySelectorAll(selector),
    querySelector: (selector: string) => root.querySelector(selector),
    getElementById: (id: string) => root.querySelectorAll('[id]').find(e => e.id === id) || null,
    createElement: (tag: string) => new FakeElement(tag)
  }
  const window = {
    get location() { return location.url },
    addEventListener: (type: string, listener: () => void) => listeners.set(type, [...(listeners.get(type) || []), listener]),
    dispatchEvent: (event: Event) => { (listeners.get(event.type) || []).forEach(l => l()); return true }
  }
  const localStorage = {
    getItem: (key: string) => items.has(key) ? items.get(key) : null,
    setItem: (key: string, value: string) => { items.set(key, value) },
    removeItem: (key: string) => { items.delete(key) }
  }
  const history = {
    replaceState: (_: unknown, __: string, to: URL | string) => { location.url = new URL(to.toString(), location.url) }
  }

  for (const [name, value] of Object.entries({ document, window, localStorage, history })) {
    Object.defineProperty(globalThis, name, { configurable: true, writable: true, value })
  }
  return { document, window, storage: items, root }
}
