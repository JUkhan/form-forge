import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { useEffect } from 'react'
import type { MutableRefObject } from 'react'

// Identity translator — `t(key)` returns the key, so buttons can be located by
// their i18n key as the accessible name.
vi.mock('react-i18next', () => ({
  useTranslation: () => ({
    t: (key: string) => key,
  }),
}))

// Hoisted handle so the vi.mock factory and individual tests share one spy
// instance. `submit.fn` is what submitRef.current() ends up calling; `onSaveProp.fn`
// captures the host's onSave wrapper so the test can verify the
// `(data) => { onSave(data); handleClose() }` ordering, not just button→ref plumbing.
const dynamicHandles = vi.hoisted(() => {
  return {
    submit: { fn: null as null | (() => void) },
    onSaveProp: {
      fn: null as null | ((data: Record<string, unknown>) => void),
    },
    reset() {
      this.submit.fn = null
      this.onSaveProp.fn = null
    },
  }
})

// Mock DynamicComponent so the unit test does NOT issue a real schema fetch.
// The mock matches the real contract: it assigns submitRef.current inside a
// useEffect (production DynamicComponent does the same), captures the host's
// onSave prop, and surfaces two test buttons that flip onReadyChange /
// onValidityChange so the host's Save-button disabled gating can be exercised
// deterministically.
vi.mock('../DynamicComponent', () => ({
  default: function MockDynamicComponent({
    submitRef,
    onReadyChange,
    onValidityChange,
    onSave,
    designerId,
    initialData,
  }: {
    submitRef?: MutableRefObject<(() => void) | null>
    onReadyChange?: (v: boolean) => void
    onValidityChange?: (v: boolean) => void
    onSave?: (data: Record<string, unknown>) => void
    designerId: string
    initialData?: Record<string, unknown>
  }) {
    useEffect(() => {
      dynamicHandles.onSaveProp.fn = onSave ?? null
    }, [onSave])
    useEffect(() => {
      if (!submitRef) return
      submitRef.current = () => dynamicHandles.submit.fn?.()
      return () => {
        submitRef.current = null
      }
    }, [submitRef])
    return (
      <div
        data-testid="mock-dynamic-component"
        data-designer-id={designerId}
        data-initial-data={JSON.stringify(initialData ?? {})}
      >
        <button
          type="button"
          data-testid="mock-set-ready"
          onClick={() => onReadyChange?.(true)}
        >
          set ready
        </button>
        <button
          type="button"
          data-testid="mock-set-valid"
          onClick={() => onValidityChange?.(true)}
        >
          set valid
        </button>
      </div>
    )
  },
}))

// Import AFTER vi.mock so the component picks up the stubbed DynamicComponent.
import RepeaterRowDrawer from '../RepeaterRowDrawer'

describe('RepeaterRowDrawer', () => {
  beforeEach(() => {
    cleanup()
    dynamicHandles.reset()
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('renders DynamicComponent when designerId is non-empty', () => {
    render(
      <RepeaterRowDrawer
        designerId="row-form"
        version={2}
        mode="add"
        initialData={{}}
        onSave={() => {}}
        onClose={() => {}}
      />,
    )
    const dc = screen.getByTestId('mock-dynamic-component')
    expect(dc).toBeTruthy()
    expect(dc.getAttribute('data-designer-id')).toBe('row-form')
  })

  it('forwards initialData to DynamicComponent in edit mode', () => {
    const initial = { name: 'Alice', age: 42 }
    render(
      <RepeaterRowDrawer
        designerId="row-form"
        version={1}
        mode="edit"
        initialData={initial}
        onSave={() => {}}
        onClose={() => {}}
      />,
    )
    const dc = screen.getByTestId('mock-dynamic-component')
    const captured = JSON.parse(dc.getAttribute('data-initial-data') ?? '{}')
    expect(captured).toEqual(initial)
  })

  it('renders empty-state message and disables Save when designerId is ""', () => {
    render(
      <RepeaterRowDrawer
        designerId=""
        version={undefined}
        mode="add"
        initialData={{}}
        onSave={() => {}}
        onClose={() => {}}
      />,
    )
    expect(
      screen.getByText('designer.repeaterDrawer.noDesigner'),
    ).toBeTruthy()
    expect(screen.queryByTestId('mock-dynamic-component')).toBeNull()
    const saveBtn = screen.getByRole('button', { name: 'designer.repeaterDrawer.save' }) as HTMLButtonElement
    expect(saveBtn.disabled).toBe(true)
  })

  it('disables Save when DynamicComponent has not signalled ready', () => {
    render(
      <RepeaterRowDrawer
        designerId="row-form"
        version={undefined}
        mode="add"
        initialData={{}}
        onSave={() => {}}
        onClose={() => {}}
      />,
    )
    // Only mark valid — ready stays false.
    fireEvent.click(screen.getByTestId('mock-set-valid'))
    const saveBtn = screen.getByRole('button', { name: 'designer.repeaterDrawer.save' }) as HTMLButtonElement
    expect(saveBtn.disabled).toBe(true)
  })

  it('disables Save when DynamicComponent reports invalid form', () => {
    render(
      <RepeaterRowDrawer
        designerId="row-form"
        version={undefined}
        mode="edit"
        initialData={{ name: 'Alice' }}
        onSave={() => {}}
        onClose={() => {}}
      />,
    )
    // Only mark ready — valid stays false.
    fireEvent.click(screen.getByTestId('mock-set-ready'))
    const saveBtn = screen.getByRole('button', { name: 'designer.repeaterDrawer.save' }) as HTMLButtonElement
    expect(saveBtn.disabled).toBe(true)
  })

  it('clicking Save calls host onSave with payload, then closes the drawer with the slide-out animation', () => {
    vi.useFakeTimers()
    const onSaveHost = vi.fn()
    const onCloseHost = vi.fn()
    const payload = { name: 'Alice' }
    // When submitRef fires, the mock invokes its captured onSave with payload —
    // this exercises the wrapper `(data) => { onSave(data); handleClose() }`.
    dynamicHandles.submit.fn = () => dynamicHandles.onSaveProp.fn?.(payload)

    const { container } = render(
      <RepeaterRowDrawer
        designerId="row-form"
        version={1}
        mode="add"
        initialData={{}}
        onSave={onSaveHost}
        onClose={onCloseHost}
      />,
    )
    fireEvent.click(screen.getByTestId('mock-set-ready'))
    fireEvent.click(screen.getByTestId('mock-set-valid'))
    const saveBtn = screen.getByRole('button', { name: 'designer.repeaterDrawer.save' }) as HTMLButtonElement
    expect(saveBtn.disabled).toBe(false)

    fireEvent.click(saveBtn)

    // Host onSave was called with the payload before the drawer started closing.
    expect(onSaveHost).toHaveBeenCalledTimes(1)
    expect(onSaveHost).toHaveBeenCalledWith(payload)

    // Drawer should be animating closed — slide-out class is on the inner panel.
    expect(container.querySelector('.translate-x-full')).toBeTruthy()

    // After the 350ms safety timer, onClose fires.
    expect(onCloseHost).not.toHaveBeenCalled()
    vi.advanceTimersByTime(350)
    expect(onCloseHost).toHaveBeenCalledTimes(1)
  })

  it('mousedown-inside-panel + drag-to-overlay + release-on-overlay does NOT close (drag-out guard, regression of patch #9)', () => {
    // Reproduces the sequence that escaped the first patch:
    //   1. mousedown on overlay → ref=true; drag into panel; release on panel
    //      (panel onClick stopPropagation swallows the overlay click → ref
    //      never reset). Ref stays true (stale).
    //   2. mousedown inside the panel (e.g. on an input); drag out; release
    //      on the overlay. Without the panel-onMouseDown ref reset, the
    //      overlay's click handler would see the stale ref=true and close
    //      the drawer, discarding edits.
    const onClose = vi.fn()
    const { container } = render(
      <RepeaterRowDrawer
        designerId="row-form"
        version={1}
        mode="add"
        initialData={{}}
        onSave={() => {}}
        onClose={onClose}
      />,
    )
    const overlay = container.firstChild as HTMLElement
    const panel = overlay.firstChild as HTMLElement

    // Sequence 1: overlay-down → panel-up. Overlay onMouseDown sets ref=true;
    // panel onClick stopPropagation swallows the overlay onClick (ref reset
    // path is bypassed).
    fireEvent.mouseDown(overlay, { target: overlay })
    fireEvent.click(panel, { target: panel })

    // Sequence 2: panel-down (input-like) → overlay-up. Panel onMouseDown
    // MUST clear the stale ref before stopping propagation; otherwise the
    // overlay onClick below would see ref=true and close.
    fireEvent.mouseDown(panel, { target: panel })
    fireEvent.click(overlay, { target: overlay })

    // Drawer must still be open — translate-x-0 indicates open, translate-x-full
    // indicates closing animation has started (which would mean the bug fired).
    expect(container.querySelector('.translate-x-0')).toBeTruthy()
    expect(container.querySelector('.translate-x-full')).toBeNull()
    expect(onClose).not.toHaveBeenCalled()
  })

  it('Escape keydown starts the closing animation and fires onClose after the safety timeout', () => {
    vi.useFakeTimers()
    const onClose = vi.fn()
    const { container } = render(
      <RepeaterRowDrawer
        designerId="row-form"
        version={undefined}
        mode="add"
        initialData={{}}
        onSave={() => {}}
        onClose={onClose}
      />,
    )
    // Pre-Escape: the inner panel is on-screen (translate-x-0).
    expect(container.querySelector('.translate-x-0')).toBeTruthy()
    fireEvent.keyDown(window, { key: 'Escape' })
    // Post-Escape: the panel is sliding off-screen (translate-x-full).
    expect(container.querySelector('.translate-x-full')).toBeTruthy()
    // onClose must NOT fire synchronously — it waits for transitionend or the timeout.
    expect(onClose).not.toHaveBeenCalled()
    // After 350ms, the safety timer fires onClose.
    vi.advanceTimersByTime(350)
    expect(onClose).toHaveBeenCalledTimes(1)
  })
})
