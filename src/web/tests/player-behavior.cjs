const { test } = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const vm = require('node:vm')
const path = require('node:path')
const ts = require('typescript')

const source = fs.readFileSync(path.join(__dirname, '../src/components/Player.tsx'), 'utf8')
const js = ts.transpileModule(source, { compilerOptions: {
  module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX,
} }).outputText

function harness({ nextEpisode = null, showNext = false } = {}) {
  const effects = [], saves = [], soundSaves = [], purges = []
  const showNextChanges = []
  let now = 0
  let resolveProgress, rejectProgress, resolveSound
  const progress = new Promise((resolve, reject) => { resolveProgress = resolve; rejectProgress = reject })
  const sound = new Promise((resolve) => { resolveSound = resolve })
  const video = {
    currentTime: 0, duration: 3600, volume: 1, muted: false, paused: true, readyState: 4,
    play() { this.paused = false; return Promise.resolve() },
    pause() { this.paused = true }, removeAttribute() {}, load() {},
  }
  let refIndex = 0
  let stateIndex = 0
  const react = {
    useState: (initial) => {
      const index = stateIndex++
      return [index === 12 ? nextEpisode : index === 14 ? showNext :
        typeof initial === 'function' ? initial() : initial,
      (value) => { if (index === 14) showNextChanges.push(value) }]
    },
    useRef: (initial) => ({ current: refIndex++ === 0 ? video : initial }),
    useCallback: (callback) => callback,
    useEffect: (effect) => effects.push(effect),
  }
  const jsx = (type, props) => ({ type, props })
  const api = {
    getProgress: () => progress,
    getPlayerSound: () => sound,
    getStreamInfo: () => Promise.resolve({ mode: 'direct', url: 'movie.mp4' }),
    getNextEpisode: () => Promise.resolve(null),
    getPreviousEpisode: () => Promise.resolve(null),
    saveProgress: (...args) => { saves.push(args); return Promise.resolve() },
    savePlayerSound: (...args) => { soundSaves.push(args); return Promise.resolve() },
    purgeSegments: (...args) => { purges.push(args); return Promise.resolve() },
    heartbeatStream: () => Promise.resolve(),
  }
  const sandbox = {
    exports: {}, crypto: { randomUUID: (() => { let i = 0; return () => `session-${++i}` })() },
    window: { setInterval: () => 1, clearInterval() {}, addEventListener() {}, removeEventListener() {} },
    document: { fullscreenElement: null },
    Date: class extends Date { static now() { return now } },
    require(name) {
      if (name === 'react') return react
      if (name === 'react/jsx-runtime') return { jsx, jsxs: jsx }
      if (name === 'hls.js') return { default: { isSupported: () => false } }
      if (name === '../api') return { api }
      if (name === '../photino') return { getPhotino: () => null, setNativeFullscreen() {} }
      return new Proxy({}, { get: (_, key) => String(key) })
    },
  }
  vm.runInNewContext(js, sandbox)
  const nextCalls = []
  const tree = sandbox.exports.Player({ mediaFileId: 11, onClose() {}, onPlayNext: (...args) => nextCalls.push(args) })
  const videoNode = tree.props.children.find((child) => child?.type === 'video')
  const relevant = effects.filter((effect) => /getProgress\(|getPlayerSound\(|getStreamInfo\(/.test(effect.toString()))
  const cleanups = relevant.map((effect) => effect())
  const flush = async () => { for (let i = 0; i < 5; i++) await Promise.resolve() }
  return { video, videoNode, tree, saves, soundSaves, purges, nextCalls, showNextChanges,
    resolveProgress, rejectProgress, advance: () => { now = 6001 },
    resolveSound, flush, cleanup: () => cleanups.forEach((cleanup) => cleanup?.()) }
}

test('late progress applies resume before play or save', async () => {
  const h = harness()
  h.resolveSound({ volume: 0.35, muted: true })
  await h.flush()
  h.videoNode.props.onLoadedMetadata({ currentTarget: h.video })
  h.video.currentTime = 6
  h.videoNode.props.onTimeUpdate({ currentTarget: h.video })
  assert.equal(h.video.paused, true)
  assert.equal(h.saves.length, 0)
  h.resolveProgress({ positionSeconds: 1500, finished: false })
  await h.flush()
  assert.equal(h.video.currentTime, 1500)
  assert.equal(h.video.paused, false)
  assert.equal(h.video.volume, 0.35)
  assert.equal(h.video.muted, true)
  h.video.currentTime = 1506
  h.advance()
  h.videoNode.props.onTimeUpdate({ currentTarget: h.video })
  assert.equal(h.saves[0][1], 1506)
  assert.equal(h.saves[0][3], 'session-1')
  h.cleanup()
  assert.equal(h.purges.length, 1)
  assert.equal(h.purges[0][1], 'session-2')
})

test('failed progress read never starts or overwrites saved position', async () => {
  const h = harness()
  h.resolveSound({ volume: 1, muted: false })
  h.videoNode.props.onLoadedMetadata({ currentTarget: h.video })
  h.rejectProgress(new Error('read failed'))
  await h.flush()
  h.video.currentTime = 6
  h.videoNode.props.onTimeUpdate({ currentTarget: h.video })
  assert.equal(h.video.paused, true)
  assert.equal(h.saves.length, 0)
  h.cleanup()
  assert.equal(h.saves.length, 0)
})

test('cancelled autoplay stays cancelled through time updates and ended', async () => {
  const h = harness({ nextEpisode: { mediaFileId: 12, season: 1, number: 2 }, showNext: true })
  const find = (node, predicate) => {
    if (!node || typeof node !== 'object') return null
    if (predicate(node)) return node
    const children = node.props?.children
    for (const child of Array.isArray(children) ? children : [children]) {
      const result = find(child, predicate)
      if (result) return result
    }
    return null
  }
  const cancel = find(h.tree, (node) => node.props?.children === 'Zrušit')
  assert.ok(cancel)
  cancel.props.onClick()
  h.video.currentTime = 3595
  h.videoNode.props.onTimeUpdate({ currentTarget: h.video })
  h.videoNode.props.onEnded()
  assert.equal(h.nextCalls.length, 0)
  assert.equal(h.showNextChanges.includes(true), false)
  h.cleanup()
})
