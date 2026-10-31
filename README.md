# Road Tool 2.0

<img width="2523" height="1288" alt="Capture d&#39;écran 2026-10-31 044604" src="https://github.com/user-attachments/assets/38e0c45c-efc5-4660-a801-32e0ba7ca2e7" />

Editor-only road building tool for s&box, built around a single road network graph (nodes + segments) from which every mesh: road chunks, junctions, sidewalks and more.

Rewritten from scratch, my previous [Road Tool](https://github.com/K3rhos/RoadTool) stays as the legacy library.

However in this version there is no traffic, vehicle or pedestrian systems (road geometry only for now)
It could easily be reimplemented honestly, but I don't have the need to for now.

## How it works

- `RoadNetwork` (Code/Network) owns the graph: `RoadNode`s joined by `RoadSegment`s, each segment a cubic Bezier with its own profile (width for now). Tangents are automatic: smooth through a pass-through node, straight at road ends and into junctions.
- Every mesh is a derived child of the network GameObject: one `MeshComponent` per chunk (`Chunk Length` units of a segment) and one per junction, each tagged with a `RoadChunk` marker that records what it was built from. A reconcile pass compares the graph with the pieces and queues only the stale ones, a few per frame (`Chunks Per Frame`). Pieces are ordinary mesh objects: editable, mergeable, and lockable one by one.
- Junctions (`RoadJunctionLayout`) are computed wherever two or more roads meet at an angle: arms are sorted around the node, adjacent road edges are intersected to find the corners, each corner gets a fillet of the node's `Corner Radius`, the arm meshes are trimmed back past the corners and the remaining area is filled with a fan from the node. A smooth pass-through node has no junction.
- Road types are **`.road` assets** (`Assets/RoadTool2/Resources/Profiles/`): surface material and texture repeat, plus the border style — `None`, `Sidewalk` (raised kerb + pavement) or `CountrySide` (road-material shoulder, a drop, then a noisy verge falling into the terrain). Each segment references a profile (the network's `Default Profile` otherwise), so a road can change type at any node. Borders are separate `border` chunks swept along the road edges and round the junction corners (each corner half in its own arm's style), with end caps wherever nothing continues them.
- Undo/redo goes through the editor undo system by snapshotting the network component; chunks catch up on their own.

## Editor

Select **Road Tool 2.0** in the scene view tools. Sub tools:

- **Draw** Click the ground to lay nodes, each click extends the road; click an existing node to continue from it or join to it; click on an existing road to branch off it (the road is split there) or to merge into it; right-click or `Esc` ends the road. Width, snap radius and sharp/smooth corners are in the sidebar.
- **Edit** Click a node or a road to select it. Drag a node to slide it along the ground, or use its move gizmo (arrows for axis/elevation moves). Drag a node's tangent handles to reshape the roads through it (`Alt` breaks the mirror to make a corner). `Ctrl`+click a road to insert a node; `Delete` removes the selection; `Esc` deselects. The sidebar edits the selected node (position, auto tangents) or road (width).
