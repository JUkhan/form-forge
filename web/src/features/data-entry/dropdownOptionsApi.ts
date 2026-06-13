import { httpClient } from '../auth/httpClient'

// One {value,label} option for a Designer-backed dropdown. value is always a
// string (the source column may be uuid/number/text); label may be null when
// the chosen label column is empty for a row.
export interface DropdownOptionDto {
  value: string
  label: string | null
}

export interface DropdownOptionsPage {
  data: DropdownOptionDto[]
  total: number
  page: number
  pageSize: number
  totalPages: number
}

export interface DropdownOptionsParams {
  version: number
  labelField: string
  valueField: string
  search?: string
  page?: number
  pageSize?: number
  // Cascading filters: target column → value (already resolved from the
  // dependsOn local:target map against the form's data).
  filter?: Record<string, string>
}

// GET /api/data/{designerId}/options — paginated {value,label} options from a
// designer's provisioned table, limited to the label + value columns.
export function fetchDropdownOptions(
  designerId: string,
  params: DropdownOptionsParams,
): Promise<DropdownOptionsPage> {
  const sp = new URLSearchParams()
  sp.set('version', String(params.version))
  sp.set('labelField', params.labelField)
  sp.set('valueField', params.valueField)
  if (params.search) sp.set('search', params.search)
  sp.set('page', String(params.page ?? 1))
  sp.set('pageSize', String(params.pageSize ?? 50))
  if (params.filter) {
    for (const [k, v] of Object.entries(params.filter)) sp.append(`filter[${k}]`, v)
  }
  return httpClient.get<DropdownOptionsPage>(
    `/api/data/${encodeURIComponent(designerId)}/options?${sp.toString()}`,
  )
}
