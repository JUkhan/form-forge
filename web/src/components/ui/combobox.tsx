"use client"

import { useState } from "react"
import { Check, ChevronsUpDown, Loader2 } from "lucide-react"

import { cn } from "@/lib/utils"
import {
  Command,
  CommandEmpty,
  CommandGroup,
  CommandInput,
  CommandItem,
  CommandList,
} from "@/components/ui/command"
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover"

export interface ComboboxOption {
  value: string
  label: string
}

interface ComboboxProps {
  value: string
  onValueChange: (value: string) => void
  options: ComboboxOption[]
  placeholder?: string
  searchPlaceholder?: string
  emptyText?: string
  disabled?: boolean
  loading?: boolean
  // When provided, the component runs in SERVER-search mode: cmdk's built-in
  // filtering is disabled and `options` is assumed already filtered for the
  // current query. When omitted, cmdk filters `options` client-side by label.
  onSearchChange?: (query: string) => void
  className?: string
  id?: string
  "aria-invalid"?: boolean
  "aria-describedby"?: string
  "aria-label"?: string
}

export function Combobox({
  value,
  onValueChange,
  options,
  placeholder = "Select…",
  searchPlaceholder = "Search…",
  emptyText = "No results.",
  disabled,
  loading,
  onSearchChange,
  className,
  id,
  "aria-invalid": ariaInvalid,
  "aria-describedby": ariaDescribedBy,
  "aria-label": ariaLabel,
}: ComboboxProps) {
  const [open, setOpen] = useState(false)
  const [query, setQuery] = useState("")
  const serverMode = onSearchChange !== undefined

  const selected = options.find((o) => o.value === value)
  // Fall back to the raw value when its label isn't in the current option page
  // (e.g. a saved value whose row isn't on the first server page).
  const triggerText = value === "" ? placeholder : (selected?.label ?? value)

  return (
    <Popover
      open={open}
      onOpenChange={(next) => {
        setOpen(next)
        if (!next && serverMode && query !== "") {
          setQuery("")
          onSearchChange?.("")
        }
      }}
    >
      <PopoverTrigger asChild>
        <button
          type="button"
          role="combobox"
          id={id}
          aria-expanded={open}
          aria-invalid={ariaInvalid}
          aria-describedby={ariaDescribedBy}
          aria-label={ariaLabel}
          disabled={disabled}
          className={cn(
            "flex h-9 w-full items-center justify-between gap-2 rounded-lg border border-field-border bg-field px-3 py-2 text-sm outline-none hover:bg-overlay-hover active:bg-overlay-active focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 disabled:pointer-events-none disabled:cursor-not-allowed disabled:opacity-50 aria-invalid:border-destructive aria-invalid:ring-3 aria-invalid:ring-destructive/20",
            className,
          )}
        >
          <span className={cn("truncate", value === "" && "text-placeholder")}>
            {triggerText}
          </span>
          <ChevronsUpDown className="h-4 w-4 shrink-0 text-muted-foreground" />
        </button>
      </PopoverTrigger>
      <PopoverContent className="w-(--radix-popover-trigger-width) p-0" align="start">
        <Command shouldFilter={!serverMode}>
          <CommandInput
            value={query}
            onValueChange={(q) => {
              setQuery(q)
              onSearchChange?.(q)
            }}
            placeholder={searchPlaceholder}
          />
          <CommandList>
            {loading ? (
              <div className="flex items-center justify-center gap-2 py-6 text-sm text-muted-foreground">
                <Loader2 className="h-4 w-4 animate-spin" />
              </div>
            ) : (
              <>
                <CommandEmpty>{emptyText}</CommandEmpty>
                <CommandGroup>
                  {options.map((o) => (
                    <CommandItem
                      // cmdk filters by this `value` in client mode — use the
                      // label so typing matches what the user sees. onSelect uses
                      // the closure's real value, not cmdk's (lowercased) arg.
                      key={o.value}
                      value={serverMode ? o.value : `${o.label} ${o.value}`}
                      onSelect={() => {
                        onValueChange(o.value)
                        setOpen(false)
                      }}
                    >
                      <Check
                        className={cn(
                          "h-4 w-4 shrink-0",
                          value === o.value ? "opacity-100" : "opacity-0",
                        )}
                      />
                      <span className="truncate">{o.label}</span>
                    </CommandItem>
                  ))}
                </CommandGroup>
              </>
            )}
          </CommandList>
        </Command>
      </PopoverContent>
    </Popover>
  )
}
