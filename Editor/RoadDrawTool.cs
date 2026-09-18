using Editor;
using Sandbox;

namespace RedSnail.RoadTool2.Editor;

/// <summary>
/// Click on the ground to lay road nodes; consecutive clicks are joined by segments. Clicking an existing node
/// continues from it or joins the road to it; clicking on an existing road splits it there and does the same, which
/// is how roads get branched off and merged into each other. Right-click or Escape ends the chain.
/// </summary>
[Title("Draw")]
[Icon("timeline")]
[Group("1")]
[Order(0)]
public class RoadDrawTool : EditorTool
{
	/// <summary>
	/// A right button release this close to where it was pressed is a click; anything further is the camera orbiting.
	/// </summary>
	private const float ClickDragTolerance = 4.0f;

	/// <summary>
	/// A split this close to a road's end reuses the end node instead of leaving a sliver of road behind.
	/// </summary>
	private const float MinEndDistance = 25.0f;

	/// <summary>
	/// What the cursor is over this frame and where the next node would go.
	/// </summary>
	private readonly struct Target
	{
		public readonly Vector3 World;
		public readonly RoadNode Node;
		public readonly RoadSegmentPick? Road;

		public Target(Vector3 _World, RoadNode _Node, RoadSegmentPick? _Road)
		{
			World = _World;
			Node = _Node;
			Road = _Road;
		}
	}

	[Property, Title("Width"), Range(50.0f, 2000.0f)] public float Width { get; set; } = 500.0f;

	/// <summary>
	/// The road type new roads get. Empty uses the network's default profile.
	/// </summary>
	[Property, Title("Profile")] public RoadProfile Profile { get; set; }

	[Property, Title("Snap Radius"), Range(0.0f, 500.0f)] public float SnapRadius { get; set; } = 150.0f;

	/// <summary>
	/// Sharp corners give straight roads meeting at an angle (grid streets); off, the road flows smoothly through
	/// every node placed.
	/// </summary>
	[Property, Title("Sharp Corners")] public bool SharpCorners { get; set; } = false;

	private int m_LastNodeId = -1;
	private Vector2 m_RightPressPosition;

	private RoadNetworkTool Root => Manager?.CurrentTool as RoadNetworkTool;

	public bool IsDrawing => m_LastNodeId >= 0;



	public override void OnDisabled()
	{
		EndChain();
	}



	public override Widget CreateToolSidebar()
	{
		return new RoadDrawToolWidget(this);
	}



	public override void OnUpdate()
	{
		var root = Root;

		if (root is null)
			return;

		var network = root.Network;

		// Undo can pull the node we were extending from out from under us.
		if (IsDrawing && (!network.IsValid() || network.FindNode(m_LastNodeId) is null))
			EndChain();

		if (!Gizmo.HasMouseFocus)
			return;

		if (Gizmo.WasRightMousePressed)
			m_RightPressPosition = Gizmo.CursorPosition;

		if (Gizmo.WasRightMouseReleased && Vector2.Distance(Gizmo.CursorPosition, m_RightPressPosition) < ClickDragTolerance)
			EndChain();

		if (!TryGetCursorPosition(network, out Vector3 cursorWorld))
			return;

		var target = ResolveTarget(network, cursorWorld);

		DrawPreview(network, target);

		if (Gizmo.WasLeftMousePressed)
			PlaceNode(root, target);
	}



	public void EndChain()
	{
		m_LastNodeId = -1;
	}



	/// <summary>
	/// Nodes win over roads, roads over bare ground. A node counts as hit when its on-screen handle is under the
	/// cursor or it lies within the snap radius of the ground point.
	/// </summary>
	private Target ResolveTarget(RoadNetwork _Network, Vector3 _CursorWorld)
	{
		if (!_Network.IsValid())
			return new Target(_CursorWorld, null, null);

		var transform = _Network.WorldTransform;
		var node = RoadPicker.PickNode(_Network, Gizmo.CurrentRay);

		if (node is null && SnapRadius > 0.0f)
			node = _Network.FindNearestNode(transform.PointToLocal(_CursorWorld), SnapRadius);

		if (node is not null)
			return new Target(transform.PointToWorld(node.Position), node, null);

		var road = RoadPicker.PickSegment(_Network, Gizmo.CurrentRay);

		if (road is { } pick)
			return new Target(transform.PointToWorld(pick.LocalPoint), null, pick);

		return new Target(_CursorWorld, null, null);
	}



	private void PlaceNode(RoadNetworkTool _Root, in Target _Target)
	{
		var session = SceneEditorSession.Active;
		var network = _Root.Network;
		bool createNetwork = !network.IsValid();

		// Picking up an existing node changes nothing in the graph (starting from it, clicking the node we are
		// already on, or joining one we are already connected to): just continue the chain from there.
		if (_Target.Node is { } existing && (!IsDrawing || existing.Id == m_LastNodeId || network.FindSegmentBetween(m_LastNodeId, existing.Id) is not null))
		{
			m_LastNodeId = existing.Id;
			return;
		}

		string undoName = _Target.Road is not null
			? (IsDrawing ? "Join Road" : "Branch Road")
			: (IsDrawing ? "Extend Road" : "Start Road");

		// A brand new network is undone by destroying its GameObject; an existing one by restoring its graph.
		var scope = session.UndoScope(undoName);

		if (createNetwork)
			scope.WithGameObjectCreations();
		else
			scope.WithComponentChanges(network);

		using (scope.Push())
		{
			if (createNetwork)
				network = _Root.CreateNetwork();

			RoadNode node;
			bool isNew = false;

			if (_Target.Node is not null)
			{
				node = _Target.Node;
			}
			else if (_Target.Road is { } pick)
			{
				node = network.SplitSegment(pick.Segment.Id, pick.T, MinEndDistance);
			}
			else
			{
				node = network.AddNode(network.WorldTransform.PointToLocal(_Target.World));
				isNew = true;
			}

			if (node is null)
				return;

			if (IsDrawing && node.Id != m_LastNodeId)
				network.AddSegment(m_LastNodeId, node.Id, Width, Profile);

			// A corner node is a node whose handles all point straight at its neighbours.
			if (isNew && SharpCorners)
				network.SetAutoTangents(node.Id, false);

			m_LastNodeId = node.Id;
		}
	}



	private bool TryGetCursorPosition(RoadNetwork _Network, out Vector3 _WorldPosition)
	{
		// The road meshes are skipped so a node placed beside a road lands on the ground, not on the road's edge.
		var physics = Trace.WithoutTags(RoadNetwork.ChunkTag).Run();

		if (physics.Hit)
		{
			_WorldPosition = physics.EndPosition;
			return true;
		}

		var meshes = MeshTrace.WithoutTags(RoadNetwork.ChunkTag).Run();

		if (meshes.Hit)
		{
			_WorldPosition = meshes.EndPosition;
			return true;
		}

		// Nothing under the cursor (empty scene, or aiming past the terrain): fall back to a ground plane through
		// the node being extended so the road can still be drawn.
		float height = 0.0f;

		if (IsDrawing && _Network.IsValid() && _Network.FindNode(m_LastNodeId) is { } last)
			height = _Network.WorldTransform.PointToWorld(last.Position).z;

		var plane = new Plane(new Vector3(0.0f, 0.0f, height), Vector3.Up);
		var hit = plane.Trace(Gizmo.CurrentRay, true);

		_WorldPosition = hit ?? default;

		return hit.HasValue;
	}



	private void DrawPreview(RoadNetwork _Network, in Target _Target)
	{
		using (Gizmo.Scope("roadtool2_draw"))
		{
			Gizmo.Draw.IgnoreDepth = true;
			Gizmo.Draw.LineThickness = 2.0f;

			if (_Target.Node is not null)
			{
				Gizmo.Draw.Color = Color.Green;
				RoadGizmos.DrawNode(_Target.World, true);
			}
			else if (_Target.Road is { } pick)
			{
				// Hovering a road: show the road and where the new node would split it.
				Gizmo.Draw.Color = Color.Orange.WithAlpha(0.6f);

				if (_Network.TryGetCurve(pick.Segment, out var roadCurve))
					RoadGizmos.DrawRoadOutline(roadCurve, pick.Segment.Width, _Network.WorldTransform);

				Gizmo.Draw.Color = Color.Orange;
				RoadGizmos.DrawNode(_Target.World, true);
			}
			else
			{
				Gizmo.Draw.Color = Color.White;
				RoadGizmos.DrawNode(_Target.World, false);
			}

			if (!IsDrawing || !_Network.IsValid())
				return;

			if (_Target.Node is not null && _Target.Node.Id == m_LastNodeId)
				return;

			var transform = _Network.WorldTransform;
			Vector3 targetLocal = _Target.Node?.Position ?? _Target.Road?.LocalPoint ?? transform.PointToLocal(_Target.World);
			var curve = _Network.PreviewCurve(m_LastNodeId, targetLocal, _Target.Node?.Id ?? -1, SharpCorners);

			Gizmo.Draw.Color = Color.White.WithAlpha(0.9f);
			RoadGizmos.DrawRoadOutline(curve, Width, transform);

			Gizmo.Draw.Color = Color.White.WithAlpha(0.4f);
			RoadGizmos.DrawCurve(curve, transform);
		}
	}
}



file class RoadDrawToolWidget : ToolSidebarWidget
{
	private readonly RoadDrawTool m_Tool;



	public RoadDrawToolWidget(RoadDrawTool _Tool)
	{
		m_Tool = _Tool;

		AddTitle("Draw Road", "timeline");

		var serialized = _Tool.GetSerialized();
		var group = AddGroup("Road");

		group.Add(ControlSheet.CreateRow(serialized.GetProperty(nameof(RoadDrawTool.Profile))));
		group.Add(ControlSheet.CreateRow(serialized.GetProperty(nameof(RoadDrawTool.Width))));
		group.Add(ControlSheet.CreateRow(serialized.GetProperty(nameof(RoadDrawTool.SnapRadius))));
		group.Add(ControlSheet.CreateRow(serialized.GetProperty(nameof(RoadDrawTool.SharpCorners))));

		AddShortcuts(
			("Place Node", "LMB"),
			("Continue From / Join Node", "LMB on node"),
			("Branch From / Merge Into Road", "LMB on road"),
			("End Road", "RMB / ESC")
		);

		Layout.AddStretchCell();
	}



	[Shortcut("roadtool2.draw-end", "ESC", typeof(SceneViewWidget))]
	private void EndChain()
	{
		m_Tool.EndChain();
	}
}
