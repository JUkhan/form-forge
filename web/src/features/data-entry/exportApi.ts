import { tokenStore } from '../auth/tokenStore'
import { refreshSession } from '../auth/refreshCoordinator'
import { generateCorrelationId } from '../../lib/correlationId'
import { ApiError } from '../../lib/api/apiError'
import type { RecordListParams } from './recordListApi'

// Story 7-followup — record list export. The endpoint streams a CSV/XLSX/PDF
// rendered server-side; this module wraps the fetch + blob + <a download>
// dance because the API requires a Bearer token, so window.open(url) (which
// would skip auth headers) is not an option.

export type ExportFormat = 'csv' | 'xlsx' | 'pdf'

function buildQuery(
  format: ExportFormat,
  params: Pick<RecordListParams, 'sort' | 'filter' | 'includeDeleted'>,
): string {
  const search = new URLSearchParams()
  search.set('format', format)
  if (params.sort) search.set('sort', params.sort)
  if (params.includeDeleted) search.set('includeDeleted', 'true')
  if (params.filter) {
    for (const [k, v] of Object.entries(params.filter)) {
      search.append(`filter[${k}]`, v)
    }
  }
  return `?${search.toString()}`
}

// Pulls the filename out of `filename="..."` or `filename*=UTF-8''...`. The
// server always sends one or the other via Results.File(fileDownloadName: ...).
function extractFilename(disposition: string, fallback: string): string {
  // RFC 5987 form first (handles non-ASCII filenames via percent-encoding).
  const star = /filename\*=(?:UTF-8'')?([^;]+)/i.exec(disposition)
  if (star) return decodeURIComponent(star[1].trim().replace(/^"|"$/g, ''))
  const plain = /filename=("([^"]+)"|([^;]+))/i.exec(disposition)
  if (plain) return (plain[2] ?? plain[3] ?? fallback).trim()
  return fallback
}

async function fetchExport(url: string): Promise<Response> {
  const headers = {
    Authorization: `Bearer ${tokenStore.get() ?? ''}`,
    'X-Correlation-ID': generateCorrelationId(),
  }
  let response = await fetch(url, { headers })
  // Same 401 → refresh → retry once shape as httpClient. We can't reuse that
  // module directly because it parses every response as JSON.
  if (response.status === 401) {
    const refreshed = await refreshSession()
    if (refreshed) {
      tokenStore.set(refreshed.accessToken)
      response = await fetch(url, {
        headers: {
          Authorization: `Bearer ${tokenStore.get() ?? ''}`,
          'X-Correlation-ID': generateCorrelationId(),
        },
      })
    } else {
      tokenStore.clear()
      window.location.replace('/login')
    }
  }
  return response
}

export async function downloadRecordExport(
  designerId: string,
  format: ExportFormat,
  params: Pick<RecordListParams, 'sort' | 'filter' | 'includeDeleted'>,
): Promise<void> {
  const url = `/api/data/${encodeURIComponent(designerId)}/export${buildQuery(format, params)}`
  const response = await fetchExport(url)
  if (!response.ok) {
    // ApiError shape matches httpClient's so callers can toast it the same way.
    let messageKey = 'errors.exportFailed'
    let code = 'EXPORT_FAILED'
    let detail = `Export failed with status ${response.status}.`
    try {
      const body = (await response.json()) as {
        code?: string
        messageKey?: string
        detail?: string
      }
      if (body.code) code = body.code
      if (body.messageKey) messageKey = body.messageKey
      if (body.detail) detail = body.detail
    } catch {
      /* response was not JSON — keep defaults */
    }
    throw new ApiError(response.status, code, messageKey, detail)
  }
  const disposition = response.headers.get('Content-Disposition') ?? ''
  const fallback = `${designerId}.${format}`
  const filename = extractFilename(disposition, fallback)
  const blob = await response.blob()
  const objectUrl = URL.createObjectURL(blob)
  try {
    const a = document.createElement('a')
    a.href = objectUrl
    a.download = filename
    document.body.appendChild(a)
    a.click()
    a.remove()
  } finally {
    URL.revokeObjectURL(objectUrl)
  }
}
