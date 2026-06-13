import { describe, it, expect, vi } from 'vitest'
import { fireEvent, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import ComponentPreviewModal from '../ComponentPreviewModal'
import type { DesignerElement } from '@/types/designer'

vi.mock('react-i18next', () => ({
  useTranslation: () => ({ t: (k: string) => k }),
}))

vi.mock('@/features/auth/httpClient', () => ({ httpClient: { get: vi.fn() } }))
vi.mock('@/features/designer/filesApi', () => ({ filesApi: { upload: vi.fn() } }))

function makeQC() {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } })
}

const SIMPLE_SCHEMA: DesignerElement = {
  id: 'root',
  type: 'Stack',
  properties: { orientation: 'vertical' },
  children: [
    {
      id: 'field-1',
      type: 'Text Input',
      properties: { label: 'Full name', bindTo: 'name' },
      children: [],
    },
  ],
}

describe('ComponentPreviewModal', () => {
  it('renders the form field via DynamicComponent (AC-1)', () => {
    render(
      <QueryClientProvider client={makeQC()}>
        <ComponentPreviewModal
          open={true}
          onClose={() => {}}
          designerId="test-designer"
          rootElement={SIMPLE_SCHEMA}
          displayName="Test Preview"
        />
      </QueryClientProvider>,
    )
    // DynamicComponent → ElementRenderer renders the TextInput leaf as an
    // interactive <input>. The static ElementRenderer path (interactive=false)
    // marks the input disabled — asserting `disabled === false` confirms we're
    // on the DynamicComponent path (AC-1).
    const input = screen.getByRole('textbox') as HTMLInputElement
    expect(input).toBeTruthy()
    expect(input.disabled).toBe(false)
  })

  it('preview is read-only — clicking a Button leaf does not trigger any external callback (AC-2)', () => {
    // Modal accepts only one external callback (onClose). DynamicComponent's
    // handlePrimaryButtonClick returns early when no onSave is passed, and the
    // modal does not pass onSave — so a Button click must not invoke onClose
    // either (onClose is for dialog dismissal, not for form buttons).
    const onClose = vi.fn()
    const BUTTON_SCHEMA: DesignerElement = {
      id: 'root',
      type: 'Stack',
      properties: { orientation: 'vertical' },
      children: [
        {
          id: 'btn-1',
          type: 'Button',
          properties: { label: 'Submit', variant: 'primary' },
          children: [],
        },
      ],
    }
    render(
      <QueryClientProvider client={makeQC()}>
        <ComponentPreviewModal
          open={true}
          onClose={onClose}
          designerId="test-designer"
          rootElement={BUTTON_SCHEMA}
          displayName="Test Preview"
        />
      </QueryClientProvider>,
    )
    const button = screen.getByRole('button', { name: 'Submit' }) as HTMLButtonElement
    // The button must be reachable on the interactive DynamicComponent path —
    // the static ElementRenderer path renders it disabled.
    expect(button.disabled).toBe(false)
    fireEvent.click(button)
    fireEvent.click(button)
    expect(onClose).not.toHaveBeenCalled()
  })

  it('shows empty-canvas fallback when rootElement is null', () => {
    render(
      <QueryClientProvider client={makeQC()}>
        <ComponentPreviewModal
          open={true}
          onClose={() => {}}
          designerId="test-designer"
          rootElement={null}
          displayName="Test Preview"
        />
      </QueryClientProvider>,
    )
    expect(screen.getByText(/nothing to preview/i)).toBeTruthy()
  })
})
