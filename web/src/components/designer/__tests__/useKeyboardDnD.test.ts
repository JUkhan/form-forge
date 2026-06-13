import { describe, it, expect } from 'vitest'
import { act, renderHook } from '@testing-library/react'
import { useKeyboardDnD } from '../useKeyboardDnD'

// The hook appends a toggling zero-width space (U+200B) to every other
// announcement so React's text-node diff is forced to fire even when the
// same message is emitted twice in a row. Tests strip the ZWSP before
// comparing so they assert against the human-readable message.
const stripZwsp = (s: string) => s.replace(/\u200B/g, '')

describe('useKeyboardDnD', () => {
  it('initial state is null pickedUp and empty announcement', () => {
    const { result } = renderHook(() => useKeyboardDnD<string>())
    expect(result.current.pickedUp).toBeNull()
    expect(result.current.announcement).toBe('')
  })

  it('pickUp sets pickedUp payload and announcement (and originator when passed)', () => {
    const { result } = renderHook(() => useKeyboardDnD<string>())
    const fakeEl = document.createElement('div')
    act(() => {
      result.current.pickUp('item', 'Picked up item.', fakeEl)
    })
    expect(result.current.pickedUp).toBe('item')
    expect(stripZwsp(result.current.announcement)).toBe('Picked up item.')
    expect(result.current.originatorRef.current).toBe(fakeEl)
  })

  it('commit after pickup clears pickedUp, updates announcement, records insertedId', () => {
    const { result } = renderHook(() => useKeyboardDnD<string>())
    act(() => {
      result.current.pickUp('item', 'Picked up item.')
    })
    act(() => {
      result.current.commit('Dropped.', 'new-id')
    })
    expect(result.current.pickedUp).toBeNull()
    expect(stripZwsp(result.current.announcement)).toBe('Dropped.')
    expect(result.current.insertedIdRef.current).toBe('new-id')
  })

  it('cancel after pickup clears pickedUp, updates announcement, clears insertedId', () => {
    const { result } = renderHook(() => useKeyboardDnD<string>())
    act(() => {
      result.current.pickUp('item', 'Picked up item.')
    })
    act(() => {
      result.current.cancel('Cancelled.')
    })
    expect(result.current.pickedUp).toBeNull()
    expect(stripZwsp(result.current.announcement)).toBe('Cancelled.')
    expect(result.current.insertedIdRef.current).toBeNull()
  })

  it('cancel without an active pickup is a no-op for state but still updates announcement (no throw)', () => {
    const { result } = renderHook(() => useKeyboardDnD<string>())
    act(() => {
      result.current.cancel('Cancelled.')
    })
    expect(result.current.pickedUp).toBeNull()
    expect(stripZwsp(result.current.announcement)).toBe('Cancelled.')
  })

  it('pickUp twice replaces the first payload with the second', () => {
    const { result } = renderHook(() => useKeyboardDnD<string>())
    act(() => {
      result.current.pickUp('first', 'Picked up first.')
    })
    act(() => {
      result.current.pickUp('second', 'Picked up second.')
    })
    expect(result.current.pickedUp).toBe('second')
    expect(stripZwsp(result.current.announcement)).toBe('Picked up second.')
  })

  it('successive identical announcements differ by a toggled zero-width space so SR re-announces', () => {
    const { result } = renderHook(() => useKeyboardDnD<string>())
    act(() => {
      result.current.cancel('Drag cancelled.')
    })
    const first = result.current.announcement
    act(() => {
      result.current.cancel('Drag cancelled.')
    })
    const second = result.current.announcement
    // The human-readable content is identical, but the raw strings differ so
    // React's text-node diff fires and screen readers re-announce.
    expect(stripZwsp(first)).toBe('Drag cancelled.')
    expect(stripZwsp(second)).toBe('Drag cancelled.')
    expect(first).not.toBe(second)
  })
})
