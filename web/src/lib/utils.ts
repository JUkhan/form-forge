import { clsx, type ClassValue } from "clsx"
import { twMerge } from "tailwind-merge"

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs))
}

// Collision-resistant unique id with an optional human-readable prefix. Backed by
// crypto.randomUUID (with a fallback for non-secure-context webviews). A plain
// incrementing counter is NOT safe here: it resets to 0 on every page load while
// persisted ids survive, so freshly minted ids collide with reloaded ones.
export function uid(prefix = ""): string {
  const rand =
    typeof crypto !== "undefined" && typeof crypto.randomUUID === "function"
      ? crypto.randomUUID()
      : "xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx".replace(/[xy]/g, (c) => {
          const r = (Math.random() * 16) | 0
          const v = c === "x" ? r : (r & 0x3) | 0x8
          return v.toString(16)
        })
  return prefix ? `${prefix}-${rand}` : rand
}
