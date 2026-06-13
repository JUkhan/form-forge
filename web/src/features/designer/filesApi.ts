import { httpClient } from '../auth/httpClient'

export const filesApi = {
  getPresignedUrl: (fileKey: string) =>
    httpClient.get<{ url: string }>(
      `/api/files/presign?key=${encodeURIComponent(fileKey)}`,
    ),

  uploadFile: ({
    file,
    designerId,
    fieldKey,
    recordId,
  }: {
    file: File
    designerId: string
    fieldKey: string
    recordId?: string
  }) => {
    const form = new FormData()
    form.append('file', file)
    form.append('designerId', designerId)
    form.append('fieldKey', fieldKey)
    if (recordId) form.append('recordId', recordId)
    return httpClient.post<{ objectKey: string }>('/api/files/upload', form)
  },

  deleteFile: (key: string) =>
    httpClient.delete(`/api/files?key=${encodeURIComponent(key)}`),
}
