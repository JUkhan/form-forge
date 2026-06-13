import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useUploadMenuIconMutation } from './menuAdminMutations'
import { ApiError } from '../../../lib/api/apiError'
import { LucideIcon, isValidLucideName } from '../../../components/icons/LucideIcon'
import type { MenuIcon } from '../../menu/types'
import { Tooltip, TooltipTrigger, TooltipContent } from '@/components/ui/tooltip'

interface IconPickerSectionProps {
  icon: MenuIcon | null
  onChange: (next: MenuIcon | null) => void
}

type IconPickerMode = 'lucide' | 'upload'

// Curated subset of lucide-react icons most useful for nav menu items. The full
// library is ~2900 icons — a flat dropdown of all of them is unusable. Admins
// who need something outside this set fall back to the manual text input.
// Names match lucide-react export names (PascalCase, no "Icon" suffix).
const COMMON_LUCIDE_ICONS: readonly string[] = [
  'Home', 'LayoutDashboard', 'Menu', 'Compass',
  'User', 'Users', 'UserCog', 'Shield', 'Lock', 'Key',
  'Database', 'Table', 'FileText', 'File', 'Folder', 'FolderOpen', 'Files',
  'BarChart3', 'LineChart', 'PieChart', 'TrendingUp', 'Activity',
  'Bell', 'Mail', 'MessageSquare', 'Phone',
  'Calendar', 'Clock',
  'Plus', 'Edit', 'Trash2', 'Save', 'Download', 'Upload', 'Search', 'Filter',
  'Eye', 'AlertCircle', 'AlertTriangle', 'Info', 'CheckCircle', 'HelpCircle',
  'Briefcase', 'Building2', 'MapPin', 'Globe',
  'Wrench', 'Settings', 'Tag', 'Bookmark', 'Star',
]

export function IconPickerSection({ icon, onChange }: IconPickerSectionProps) {
  const { t } = useTranslation()
  const uploadMutation = useUploadMenuIconMutation()
  const [mode, setMode] = useState<IconPickerMode>('lucide')
  const [iconSearch, setIconSearch] = useState('')
  const [iconNameInput, setIconNameInput] = useState('')
  const [uploadError, setUploadError] = useState<string | null>(null)

  const currentLucideName = icon?.type === 'lucide' ? icon.name ?? null : null
  const filteredIcons = (() => {
    const q = iconSearch.trim().toLowerCase()
    if (q.length === 0) return COMMON_LUCIDE_ICONS
    return COMMON_LUCIDE_ICONS.filter((name) => name.toLowerCase().includes(q))
  })()

  const onSetLucide = () => {
    const trimmed = iconNameInput.trim()
    if (trimmed.length === 0) return
    onChange({ type: 'lucide', name: trimmed })
  }

  const onUpload = async (e: React.ChangeEvent<HTMLInputElement>) => {
    setUploadError(null)
    const file = e.target.files?.[0]
    if (!file) return
    try {
      const result = await uploadMutation.mutateAsync(file)
      onChange({ type: 'minio', objectKey: result.objectKey })
    } catch (err) {
      if (err instanceof ApiError && err.status === 422 && err.code === 'UPLOAD_INVALID') {
        setUploadError(t('admin.menus.uploadInvalid'))
        return
      }
      setUploadError(t('admin.menus.saveError'))
    } finally {
      // Reset the input so re-selecting the same file fires onChange again.
      e.target.value = ''
    }
  }

  const isUploading = uploadMutation.isPending

  return (
    <fieldset
      style={{
        display: 'flex',
        flexDirection: 'column',
        gap: '0.75rem',
        padding: '0.75rem',
        border: '1px solid var(--border)',
        borderRadius: '0.4rem',
      }}
    >
      <legend style={{ padding: '0 0.25rem', fontWeight: 600 }}>{t('admin.menus.iconSectionTitle')}</legend>

      <div style={{ display: 'flex', alignItems: 'center', gap: '0.75rem' }}>
        {icon === null && <span style={{ color: 'var(--muted-foreground)' }}>{t('admin.menus.iconPlaceholder')}</span>}
        {icon?.type === 'lucide' && icon.name && (
          <>
            <LucideIcon name={icon.name} size={24} aria-label={t('admin.menus.iconPreviewAlt')} />
            <span style={{ fontSize: '0.85rem' }}>{t('admin.menus.iconCurrentLucide', { name: icon.name })}</span>
          </>
        )}
        {icon?.type === 'minio' && (
          <span style={{ fontSize: '0.85rem' }}>{t('admin.menus.iconCurrentMinio')}</span>
        )}
        {icon !== null && (
          <button
            type="button"
            onClick={() => {
              onChange(null)
            }}
            style={{ marginLeft: 'auto' }}
          >
            {t('admin.menus.removeIconButton')}
          </button>
        )}
      </div>

      <div role="tablist" style={{ display: 'flex', gap: '0.5rem' }}>
        <button
          type="button"
          role="tab"
          aria-selected={mode === 'lucide'}
          onClick={() => {
            setMode('lucide')
          }}
          style={{ fontWeight: mode === 'lucide' ? 600 : 400 }}
        >
          {t('admin.menus.iconTabLucide')}
        </button>
        <button
          type="button"
          role="tab"
          aria-selected={mode === 'upload'}
          onClick={() => {
            setMode('upload')
          }}
          style={{ fontWeight: mode === 'upload' ? 600 : 400 }}
        >
          {t('admin.menus.iconTabUpload')}
        </button>
      </div>

      {mode === 'lucide' && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: '0.5rem' }}>
          <input
            type="search"
            placeholder={t('admin.menus.iconSearchPlaceholder')}
            value={iconSearch}
            onChange={(e) => {
              setIconSearch(e.target.value)
            }}
            aria-label={t('admin.menus.iconSearchPlaceholder')}
          />
          {filteredIcons.length === 0 ? (
            <p style={{ color: 'var(--muted-foreground)', fontSize: '0.85rem', margin: 0 }}>
              {t('admin.menus.iconNoMatch')}
            </p>
          ) : (
            <div
              role="listbox"
              aria-label={t('admin.menus.iconGridLabel')}
              style={{
                display: 'grid',
                gridTemplateColumns: 'repeat(auto-fill, minmax(72px, 1fr))',
                gap: '0.25rem',
              }}
            >
              {filteredIcons.map((name) => {
                const selected = currentLucideName === name
                return (
                  <Tooltip key={name}>
                    <TooltipTrigger asChild>
                      <button
                        type="button"
                        role="option"
                        aria-selected={selected}
                        aria-label={name}
                        onClick={() => {
                          onChange({ type: 'lucide', name })
                        }}
                        style={{
                          display: 'flex',
                          flexDirection: 'column',
                          alignItems: 'center',
                          justifyContent: 'center',
                          gap: '0.25rem',
                          padding: '0.4rem 0.25rem',
                          border: selected ? '2px solid var(--primary)' : '1px solid var(--border)',
                          borderRadius: '0.3rem',
                          backgroundColor: selected ? 'color-mix(in oklab, var(--primary) 12%, transparent)' : 'var(--card)',
                          cursor: 'pointer',
                          minHeight: '64px',
                        }}
                      >
                        <LucideIcon name={name} size={20} aria-hidden />
                        <span
                          style={{
                            fontSize: '0.65rem',
                            color: 'var(--muted-foreground)',
                            overflow: 'hidden',
                            textOverflow: 'ellipsis',
                            whiteSpace: 'nowrap',
                            width: '100%',
                            textAlign: 'center',
                          }}
                        >
                          {name}
                        </span>
                      </button>
                    </TooltipTrigger>
                    <TooltipContent>{name}</TooltipContent>
                  </Tooltip>
                )
              })}
            </div>
          )}

          {/* Fallback for icons outside the curated set. Collapsed by default so
              it doesn't clutter the common case but stays reachable. */}
          <details style={{ marginTop: '0.5rem' }}>
            <summary style={{ cursor: 'pointer', fontSize: '0.85rem', color: 'var(--muted-foreground)' }}>
              {t('admin.menus.iconManualToggle')}
            </summary>
            <div style={{ display: 'flex', gap: '0.5rem', alignItems: 'center', marginTop: '0.4rem' }}>
              <label htmlFor="lucide-icon-name" style={{ fontSize: '0.85rem' }}>
                {t('admin.menus.iconNameLabel')}
              </label>
              <input
                id="lucide-icon-name"
                type="text"
                placeholder={t('admin.menus.iconNamePlaceholder')}
                value={iconNameInput}
                onChange={(e) => {
                  setIconNameInput(e.target.value)
                }}
              />
              {iconNameInput.trim().length > 0 && (
                <LucideIcon
                  name={iconNameInput.trim()}
                  size={20}
                  aria-label={t('admin.menus.iconPreviewAlt')}
                />
              )}
              <button
                type="button"
                onClick={onSetLucide}
                disabled={!isValidLucideName(iconNameInput.trim())}
              >
                {t('admin.menus.setLucideIconButton')}
              </button>
            </div>
          </details>
        </div>
      )}

      {mode === 'upload' && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: '0.5rem' }}>
          <label htmlFor="upload-icon-file">
            {isUploading ? t('admin.menus.uploadingIconButton') : t('admin.menus.uploadIconButton')}
          </label>
          <input
            id="upload-icon-file"
            type="file"
            accept="image/png,image/jpeg"
            disabled={isUploading}
            onChange={(e) => {
              void onUpload(e)
            }}
          />
          {uploadError && (
            <span role="alert" style={{ color: 'var(--destructive)', fontSize: '0.85rem' }}>
              {uploadError}
            </span>
          )}
        </div>
      )}
    </fieldset>
  )
}
