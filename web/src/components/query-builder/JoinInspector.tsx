import { X } from 'lucide-react'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import type { JoinEdgeType } from './JoinEdge'
import type { TableNodeType } from './TableNode'
import type { JoinType } from '../../features/datasets/types/builderState'

interface JoinInspectorProps {
  edge: JoinEdgeType
  nodes: TableNodeType[]
  onJoinTypeChange: (edgeId: string, joinType: JoinType) => void
  onClose: () => void
}

const JOIN_TYPE_OPTIONS: { value: JoinType; label: string }[] = [
  { value: 'INNER', label: 'INNER' },
  { value: 'LEFT', label: 'LEFT' },
  { value: 'RIGHT', label: 'RIGHT' },
  { value: 'FULL OUTER', label: 'FULL OUTER' },
]

export function JoinInspector({ edge, nodes, onJoinTypeChange, onClose }: JoinInspectorProps) {
  const sourceNode = nodes.find((n) => n.id === edge.source)
  const targetNode = nodes.find((n) => n.id === edge.target)

  // Graceful fallback if node was removed while inspector is open
  const sourceSide = sourceNode?.data.side === 'left' ? 'Left' : 'Right'
  const targetSide = targetNode?.data.side === 'left' ? 'Left' : 'Right'
  const sourceLabel = sourceNode
    ? `${sourceNode.data.tableName} . ${edge.sourceHandle}`
    : `(removed) . ${edge.sourceHandle}`
  const targetLabel = targetNode
    ? `${targetNode.data.tableName} . ${edge.targetHandle}`
    : `(removed) . ${edge.targetHandle}`

  return (
    <div
      className="nodrag nopan absolute right-4 top-4 z-50 w-64 rounded-lg border border-border bg-card text-card-foreground shadow-lg"
      role="complementary"
      aria-label="Join inspector"
    >
      {/* Header */}
      <div className="flex items-center justify-between border-b border-border px-3 py-2">
        <span className="text-sm font-semibold">Join</span>
        <button
          type="button"
          onClick={onClose}
          className="flex h-5 w-5 items-center justify-center rounded text-muted-foreground hover:bg-overlay-hover hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          aria-label="Close inspector"
        >
          <X className="h-3.5 w-3.5" />
        </button>
      </div>

      <div className="space-y-3 p-3">
        {/* Joined columns — AC-1(a), AC-3 */}
        <div className="space-y-1.5">
          <p className="text-xs font-medium text-muted-foreground">Joined columns</p>
          <div className="space-y-1 rounded-md bg-muted px-2.5 py-2 text-xs">
            <div className="flex items-center gap-1.5">
              <span className="shrink-0 rounded bg-background px-1 py-0.5 text-xs font-medium text-muted-foreground">
                {sourceSide}
              </span>
              <span className="truncate font-mono">{sourceLabel}</span>
            </div>
            <div className="border-t border-border pt-1 flex items-center gap-1.5">
              <span className="shrink-0 rounded bg-background px-1 py-0.5 text-xs font-medium text-muted-foreground">
                {targetSide}
              </span>
              <span className="truncate font-mono">{targetLabel}</span>
            </div>
          </div>
        </div>

        {/* Join type selector — AC-1(b), AC-2, AC-4 */}
        <div className="space-y-1.5">
          <p className="text-xs font-medium text-muted-foreground">Join type</p>
          <Select
            value={edge.data?.joinType ?? 'INNER'}
            onValueChange={(value) => onJoinTypeChange(edge.id, value as JoinType)}
          >
            <SelectTrigger className="w-full" size="sm" aria-label="Join type">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {JOIN_TYPE_OPTIONS.map((opt) => (
                <SelectItem key={opt.value} value={opt.value}>
                  {opt.label}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
      </div>
    </div>
  )
}
