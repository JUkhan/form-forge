// Keyboard-driven drag-and-drop state hook. The pointer-driven HTML5 DnD
// pipeline in DesignerCanvas remains the source of truth for the actual drop
// callback (onDrop is shared); this hook only tracks the "what is currently
// picked up?" + "what announcement should the aria-live region read?" state so
// keyboard users can drive the same insertion path.
//
// The hook is intentionally generic — it must not import from `./dnd`,
// `@/store/designerCanvas`, or any other designer-specific module so Epic 4
// can reuse it for menu reordering with its own drag-data shape (see AC-6 of
// Story 3.10). Callers translate the announcement strings via their own
// `useTranslation` before calling pickUp/commit/cancel.
import { useCallback, useRef, useState } from 'react'

// Zero-width space (U+200B) — ignored by every major screen reader but counts
// as a different string for React's text-node diff, so toggling it every other
// announce() forces the aria-live region to re-fire even for identical
// successive messages.
const ZWSP = '\u200B'

function useKeyboardDnD<T>() {
  const [pickedUp, setPickedUp] = useState<T | null>(null)
  const [announcement, setAnnouncement] = useState<string>('')

  const tokenRef = useRef(0)

  // Originator is the focusable element where pickup started. `cancel`
  // restores focus to it; `commit` walks to the newly-inserted element
  // instead when the caller passes its id.
  const originatorRef = useRef<HTMLElement | null>(null)
  const insertedIdRef = useRef<string | null>(null)

  const announce = useCallback((message: string) => {
    tokenRef.current += 1
    setAnnouncement(tokenRef.current % 2 === 0 ? message : message + ZWSP)
  }, [])

  const pickUp = useCallback(
    (data: T, message: string, originator?: HTMLElement | null) => {
      originatorRef.current = originator ?? null
      insertedIdRef.current = null
      setPickedUp(data)
      announce(message)
    },
    [announce],
  )

  const commit = useCallback(
    (message: string, insertedElementId?: string | null) => {
      insertedIdRef.current = insertedElementId ?? null
      setPickedUp(null)
      announce(message)
    },
    [announce],
  )

  const cancel = useCallback(
    (message: string) => {
      insertedIdRef.current = null
      setPickedUp(null)
      announce(message)
    },
    [announce],
  )

  return {
    pickedUp,
    announcement,
    pickUp,
    commit,
    cancel,
    originatorRef,
    insertedIdRef,
  }
}

type UseKeyboardDnDReturn<T> = ReturnType<typeof useKeyboardDnD<T>>

export { useKeyboardDnD }
export type { UseKeyboardDnDReturn }
