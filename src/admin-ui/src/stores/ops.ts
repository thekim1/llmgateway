import { defineStore } from 'pinia'
import { api } from '@/api'
import type {
  AuditEntry,
  AuditQuery,
  CircuitState,
  ConfigDocument,
  ConfigImportResult,
  OpsHealth,
  Paged,
} from '@/api/types'
import { useLoadable } from './loadable'

export const useOpsStore = defineStore('ops', () => {
  const health = useLoadable<OpsHealth | null>(null)
  const audit = useLoadable<Paged<AuditEntry>>({ items: [], total: 0 })

  const loadHealth = () => health.run(() => api.ops.health())
  const loadAudit = (query: AuditQuery) => audit.run(() => api.audit.list(query))

  async function setCircuit(providerId: string, state: CircuitState): Promise<void> {
    await api.ops.setCircuit(providerId, state)
    await loadHealth()
  }

  const invalidateKeyCache = () => api.ops.invalidateKeyCache()
  const exportConfig = () => api.ops.exportConfig()
  const importConfig = (doc: ConfigDocument): Promise<ConfigImportResult> => api.ops.importConfig(doc)

  return {
    health: health.data,
    healthLoading: health.loading,
    healthError: health.error,
    audit: audit.data,
    auditLoading: audit.loading,
    auditError: audit.error,
    loadHealth,
    loadAudit,
    setCircuit,
    invalidateKeyCache,
    exportConfig,
    importConfig,
  }
})
