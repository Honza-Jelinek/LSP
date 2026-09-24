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

function harness({ nextEpisode = null, showNext = false, hls = false, holdPause = false } = {}) {
  const effects = [], saves = [], soundSaves = [], purges = [], heartbeats = [], intervals = []
  const showNextChanges = []
  const hlsCalls = []
  class HlsFake {
    static isSupported() { return hls }
    static Events = { ERROR: 'error' }
    loadSource() {}
    attachMedia() {}
    on() {}
    stopLoad() { hlsCalls.push('stop') }
    startLoad(position) { hlsCalls.push(['start', position]) }
    destroy() {}
  }
  let now = 0
  let resolveProgress, rejectProgress, resolveSound
  let releasePause
  const pauseResponse = new Promise((resolve) => { releasePause = resolve })
  const progress = new Promise((resolve, reject) => { resolveProgress = resolve; rejectProgress = reject })
  const sound = new Promise((resolve) => { resolveSound = resolve })
  const listeners = new Map()
  const video = {
    currentTime: 0, duration: 3600, volume: 1, muted: false, paused: true, readyState: 4,
    play() { this.paused = false; return Promise.resolve() },
    pause() { this.paused = true }, removeAttribute() {}, load() { this.currentTime = 0 },
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
    getStreamInfo: () => Promise.resolve({ mode: hls ? 'hls' : 'direct', url: 'movie.mp4' }),
    getNextEpisode: () => Promise.resolve(null),
    getPreviousEpisode: () => Promise.resolve(null),
    saveProgress: (...args) => { saves.push(args); return Promise.resolve() },
    savePlayerSound: (...args) => { soundSaves.push(args); return Promise.resolve() },
    purgeSegments: (...args) => { purges.push(args); return Promise.resolve() },
    heartbeatStream: (...args) => { heartbeats.push(args); return holdPause && args[2] ? pauseResponse : Promise.resolve() },
  }
  const sandbox = {
    exports: {}, crypto: { randomUUID: (() => { let i = 0; return () => `session-${++i}` })() },
    performance: { timeOrigin: 1000, now: () => now }, AbortController,
    window: { setInterval: (callback) => { intervals.push(callback); return 1 }, clearInterval() {},
      setTimeout: () => 1, clearTimeout() {},
      addEventListener: (name, callback) => listeners.set(name, callback),
      removeEventListener: (name) => listeners.delete(name) },
    document: { fullscreenElement: null },
    Date: class extends Date { static now() { return now } },
    require(name) {
      if (name === 'react') return react
      if (name === 'react/jsx-runtime') return { jsx, jsxs: jsx }
      if (name === 'hls.js') return HlsFake
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
  const flush = async () => { for (let i = 0; i < 20; i++) await Promise.resolve() }
  return { video, videoNode, tree, saves, soundSaves, purges, nextCalls, showNextChanges,
    hlsCalls, heartbeats, heartbeat: () => intervals[0]?.(),
    releasePause,
    resolveProgress, rejectProgress, advance: () => { now = 6001 },
    resolveSound, flush, pagehide: () => listeners.get('pagehide')?.(),
    retryProgress: () => relevant[0](),
    cleanup: () => cleanups.forEach((cleanup) => cleanup?.()) }
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
  h.resolveProgress(null)
  h.resolveSound({ volume: 1, muted: false })
  h.videoNode.props.onLoadedMetadata({ currentTarget: h.video })
  await h.flush()
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

test('autoplay does not appear before the initial position is applied', async () => {
  const h = harness({ nextEpisode: { mediaFileId: 12, season: 1, number: 2 } })
  h.resolveSound({ volume: 1, muted: false })
  h.videoNode.props.onLoadedMetadata({ currentTarget: h.video })
  h.video.currentTime = 3595
  h.videoNode.props.onTimeUpdate({ currentTarget: h.video })
  h.videoNode.props.onEnded()
  assert.equal(h.showNextChanges.includes(true), false)
  assert.equal(h.nextCalls.length, 0)
  h.cleanup()
})

test('effect cleanup saves the captured position before unloading the video', async () => {
  const h = harness()
  h.resolveProgress(null)
  h.resolveSound({ volume: 1, muted: false })
  h.videoNode.props.onLoadedMetadata({ currentTarget: h.video })
  await h.flush()
  h.video.currentTime = 451
  h.cleanup()
  assert.equal(h.video.currentTime, 0)
  assert.equal(h.saves.length, 1)
  assert.equal(h.saves[0][1], 451)
  assert.equal(h.saves[0][5], true)
})

test('pagehide saves once and cleanup does not duplicate the same position', async () => {
  const h = harness()
  h.resolveProgress(null)
  h.resolveSound({ volume: 1, muted: false })
  h.videoNode.props.onLoadedMetadata({ currentTarget: h.video })
  await h.flush()
  h.video.currentTime = 518
  h.pagehide()
  h.cleanup()
  assert.equal(h.saves.length, 1)
  assert.equal(h.saves[0][1], 518)
})

test('progress retry retains loaded sound and waits for resume seek completion', async () => {
  const h = harness()
  h.resolveSound({ volume: 0.2, muted: false })
  h.videoNode.props.onLoadedMetadata({ currentTarget: h.video })
  h.video.seeking = true
  h.resolveProgress({ positionSeconds: 900, finished: false })
  await h.flush()
  assert.equal(h.video.paused, true)
  assert.equal(h.video.currentTime, 900)
  assert.equal(h.saves.length, 0)
  h.video.seeking = false
  h.videoNode.props.onSeeked({ currentTarget: h.video })
  assert.equal(h.video.paused, false)
  h.video.pause()
  h.retryProgress()
  await h.flush()
  assert.equal(h.video.paused, false)
  h.cleanup()
})

test('HLS pause stops loading and buffering heartbeat remains active', async () => {
  const h = harness({ hls: true })
  h.resolveProgress(null)
  h.resolveSound({ volume: 1, muted: false })
  await h.flush()
  assert.equal(h.heartbeats.at(-1)[2], false)
  h.videoNode.props.onLoadedMetadata({ currentTarget: h.video })
  h.videoNode.props.onPlaying({ currentTarget: h.video })
  h.video.readyState = 2
  h.heartbeat()
  await h.flush()
  assert.equal(h.heartbeats.at(-1)[2], false)
  h.video.currentTime = 200
  h.video.pause()
  h.videoNode.props.onPause({ currentTarget: h.video })
  await h.flush()
  assert.equal(h.hlsCalls.at(-1), 'stop')
  assert.equal(h.heartbeats.at(-1)[2], true)
  h.video.currentTime = 300
  h.videoNode.props.onSeeking({ currentTarget: h.video })
  await h.flush()
  assert.equal(h.hlsCalls.at(-1)[0], 'start')
  assert.equal(h.hlsCalls.at(-1)[1], 300)
  h.videoNode.props.onSeeked({ currentTarget: h.video })
  await h.flush()
  assert.equal(h.hlsCalls.at(-1), 'stop')
  assert.equal(h.heartbeats.at(-1)[2], true)
  h.video.play()
  h.videoNode.props.onPlay({ currentTarget: h.video })
  await h.flush()
  assert.equal(h.heartbeats.at(-1)[2], false)
  assert.equal(h.hlsCalls.at(-1)[0], 'start')
  assert.equal(h.hlsCalls.at(-1)[1], 300)
  h.cleanup()
})

test('HLS resume heartbeat waits behind an in-flight pause heartbeat', async () => {
  const h = harness({ hls: true, holdPause: true })
  h.resolveProgress(null)
  h.resolveSound({ volume: 1, muted: false })
  await h.flush()
  h.videoNode.props.onLoadedMetadata({ currentTarget: h.video })
  h.video.pause()
  h.videoNode.props.onPause({ currentTarget: h.video })
  await h.flush()
  const beforeResume = h.heartbeats.length
  h.video.play()
  h.videoNode.props.onPlay({ currentTarget: h.video })
  await h.flush()
  assert.equal(h.heartbeats.length, beforeResume)
  h.releasePause()
  await h.flush()
  assert.equal(h.heartbeats.at(-1)[2], false)
  h.cleanup()
})

test('paused HLS seek keeps loading until the target frame is available', async () => {
  const h = harness({ hls: true })
  h.resolveProgress(null)
  h.resolveSound({ volume: 1, muted: false })
  await h.flush()
  h.videoNode.props.onLoadedMetadata({ currentTarget: h.video })
  h.video.pause()
  h.videoNode.props.onPause({ currentTarget: h.video })
  h.video.currentTime = 2000
  h.video.readyState = 1
  h.videoNode.props.onSeeking({ currentTarget: h.video })
  h.videoNode.props.onSeeked({ currentTarget: h.video })
  assert.equal(h.hlsCalls.at(-1)[0], 'start')
  h.video.readyState = 2
  h.videoNode.props.onLoadedData({ currentTarget: h.video })
  assert.equal(h.hlsCalls.at(-1), 'stop')
  h.cleanup()
})
