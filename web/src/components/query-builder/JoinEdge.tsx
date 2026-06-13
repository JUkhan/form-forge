import { memo } from 'react'
import {
  BaseEdge,
  EdgeLabelRenderer,
  getBezierPath,
  useReactFlow,
  type Edge,
  type EdgeProps,
} from '@xyflow/react'
import type { JoinEdgeData } from '../../features/datasets/types/builderState'

// v12 edge type: Edge<data, typeDiscriminator> — mirrors the TableNodeType pattern.
// JoinEdgeData is a `type` alias (not `interface`) to satisfy the
// Edge<D extends Record<string,unknown>> constraint (see Memory: @xyflow/react v12 typing gotchas).
export type JoinEdgeType = Edge<JoinEdgeData, 'joinEdge'>

export const JoinEdgeComponent = memo(function JoinEdgeComponent({
  id,
  sourceX,
  sourceY,
  targetX,
  targetY,
  sourcePosition,
  targetPosition,
  selected,
}: EdgeProps<JoinEdgeType>) {
  const { deleteElements } = useReactFlow()

  const [edgePath, labelX, labelY] = getBezierPath({
    sourceX,
    sourceY,
    sourcePosition,
    targetX,
    targetY,
    targetPosition,
  })

  return (
    <>
      <BaseEdge
        id={id}
        path={edgePath}
        style={{
          // Theme tokens are oklch(...) values, so they must be referenced directly —
          // hsl(var(--token)) yields hsl(oklch(...)), an invalid color that renders the
          // edge with no visible stroke (the join looked like it vanished on mouse-up).
          stroke: selected ? 'var(--primary)' : 'var(--muted-foreground)',
          strokeWidth: selected ? 2 : 1.5,
        }}
      />
      {selected && (
        <EdgeLabelRenderer>
          <button
            style={{
              position: 'absolute',
              transform: `translate(-50%, -50%) translate(${labelX}px,${labelY}px)`,
              pointerEvents: 'all',
            }}
            className="nodrag nopan flex h-5 w-5 items-center justify-center rounded-full bg-destructive text-xs text-destructive-foreground shadow hover:bg-destructive/90"
            type="button"
            onClick={(e) => {
              e.stopPropagation()
              deleteElements({ edges: [{ id }] })
            }}
            aria-label="Remove join"
          >
            ×
          </button>
        </EdgeLabelRenderer>
      )}
    </>
  )
})
