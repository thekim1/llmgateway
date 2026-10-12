import { computed } from 'vue'
import { defineStore } from 'pinia'
import { api } from '@/api'
import type { McpServer, McpServerRequest, McpSyncResult, McpTestResult } from '@/api/types'
import { removeById, replaceById, useLoadable } from './loadable'

/** MCP servers the user can see. Tools and grants are loaded by the components that show them. */
export const useMcpStore = defineStore('mcp', () => {
  const { data: servers, loading, error, loaded, run } = useLoadable<McpServer[]>([])

  const load = () => run(() => api.mcp.list())

  /** Tools waiting for an admin (new or changed upstream) on servers the user may manage. */
  const awaitingApproval = computed(() => servers.value.filter((s) => s.canManage).reduce((n, s) => n + s.pendingToolCount + s.changedToolCount, 0))

  async function create(request: McpServerRequest): Promise<McpServer> {
    const created = await api.mcp.create(request)
    servers.value = replaceById(servers.value, created)
    return created
  }

  async function update(id: string, request: McpServerRequest): Promise<McpServer> {
    const updated = await api.mcp.update(id, request)
    servers.value = replaceById(servers.value, updated)
    return updated
  }

  async function remove(id: string): Promise<void> {
    await api.mcp.remove(id)
    servers.value = removeById(servers.value, id)
  }

  const test = (id: string | null, request?: McpServerRequest): Promise<McpTestResult> => api.mcp.test(id, request)

  /** Syncs the server, then reloads the list so status and tool counts are current. */
  async function sync(id: string): Promise<McpSyncResult> {
    const result = await api.mcp.sync(id)
    await load()
    return result
  }

  return { servers, loading, error, loaded, awaitingApproval, load, create, update, remove, test, sync }
})

/** The request that saves `server` unchanged apart from `changes`; the stored credential is kept. */
export function toRequest(server: McpServer, changes: Partial<McpServerRequest> = {}): McpServerRequest {
  return {
    slug: server.slug,
    displayName: server.displayName,
    description: server.description,
    url: server.url,
    residency: server.residency,
    authType: server.authType,
    isEnabled: server.isEnabled,
    ownerDepartmentId: server.ownerDepartmentId,
    forwardHeaders: [...server.forwardHeaders],
    timeoutSeconds: server.timeoutSeconds,
    maxResultBytes: server.maxResultBytes,
    autoApproveNewTools: server.autoApproveNewTools,
    ...changes,
  }
}
