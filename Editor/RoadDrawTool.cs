using System;
using System.Collections.Generic;
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

	/// <summary>
	/// Set, new roads are drawn as bridges in this style. The Edit tool changes it per road afterwards.
	/// </summary>
	[Property, Title("Bridge")] public BridgeStyle Bridge { get; set; }

	/// <summary>
	/// What new roads get standing along them (lamp posts, trees...). The Edit tool changes it per road afterwards.
	/// </summary>
	[Property, Title("Props")] public RoadPropSet Props { get; set; }

	[Property, Title("Snap Radius"), Range(0.0f, 500.0f)] public float SnapRadius { get; set; } = 150.0f;

	/// <summary>
	/// Lanes new roads get: two-way with the odd lane running the way the road is drawn, or one-way that way.
	/// The Lanes tool changes them afterwards.
	/// </summary>
	[Property, Title("Lanes"), Range(RoadSegment.MinLanes, RoadSegment.MaxLanes)] public int Lanes { get; set { field = value.Clamp(RoadSegment.MinLanes, RoadSegment.MaxLanes); } } = 2;

	[Property, Title("One Way")] public bool OneWay { get; set; } = false;

	/// <summary>
	/// Sharp corners give straight roads meeting at an angle (grid streets); off, the road flows smoothly through
	/// every node placed.
	/// </summary>
	[Property, Title("Sharp Corners")] public bool SharpCorners { get; set; } = false;

	/// <summary>
	/// Snap the direction of the road being drawn to whole steps of <see cref="AngleStep"/>, measured from the
	/// world axes and from the roads already at the node being extended. Holding Shift flips it for one placement.
	/// </summary>
	[Property, Title("Angle Snap")] public bool AngleSnap { get; set; } = false;

	[Property, Title("Snap Angle"), Range(1.0f, 90.0f)] public float AngleStep { get; set; } = 10.0f;

	/// <summary>
	/// How far the snapped direction is drawn out as a guide while placing.
	/// </summary>
	private const float GuideLength = 4000.0f;

	private int m_LastNodeId = -1;
	private Vector2 m_RightPressPosition;

	/// <summary>
	/// The angle the current placement snapped to, for the guide; null when not snapping.
	/// </summary>
	private float? m_SnappedAngle;

	/// <summary>
	/// The node the current placement is aligned with, for the guide; null when not aligned.
	/// </summary>
	private Vector3? m_AlignAnchor;

	private RoadNetworkTool Root => Manager?.CurrentTool as RoadNetworkTool;

	public bool IsDrawing => m_LastNodeId >= 0;

	private bool SnapActive => AngleSnap != Gizmo.IsShiftPressed;



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

		m_SnappedAngle = null;
		m_AlignAnchor = null;

		// Joining a node is explicit and wins. Otherwise the snap places the end: on bare ground, or on a road the
		// snapped line crosses (the hovered road above all), which is split right there so the merge is straight.
		if (target.Node is null && IsDrawing && SnapActive && TrySnapAngle(network, cursorWorld, target.Road, out Vector3 snappedWorld, out float angle, out RoadSegmentPick? split))
		{
			if (split is { } pick)
			{
				target = new Target(snappedWorld, null, pick);
				m_SnappedAngle = angle;
			}
			else if (target.Road is null)
			{
				// The snapped end landing on a node joins it, like clicking the node would.
				var joined = network.FindNearestNode(network.WorldTransform.PointToLocal(snappedWorld), SnapRadius);

				target = joined is not null && joined.Id != m_LastNodeId
					? new Target(network.WorldTransform.PointToWorld(joined.Position), joined, null)
					: new Target(snappedWorld, null, null);

				m_SnappedAngle = angle;
			}
			else
			{
				// Hovering a road the snapped line never reaches: the plain join, and no alignment guide.
				m_AlignAnchor = null;
			}
		}

		DrawPreview(network, target);

		if (Gizmo.WasLeftMousePressed)
			PlaceNode(root, target);
	}



	/// <summary>
	/// Snaps the direction from the node being extended to the cursor onto the nearest of the snap angles: whole
	/// steps round from the world X axis, and whole steps round from each road already leaving that node (so a
	/// branch can be exactly square to, or straight on from, a road at any angle). The cursor's distance along the
	/// snapped direction is kept and the point is dropped back onto the ground there.
	/// </summary>
	private bool TrySnapAngle(RoadNetwork _Network, Vector3 _CursorWorld, RoadSegmentPick? _Hovered, out Vector3 _SnappedWorld, out float _Angle, out RoadSegmentPick? _Split)
	{
		_SnappedWorld = _CursorWorld;
		_Angle = 0.0f;
		_Split = null;

		if (!_Network.IsValid() || _Network.FindNode(m_LastNodeId) is not { } last)
			return false;

		var transform = _Network.WorldTransform;
		Vector3 startWorld = transform.PointToWorld(last.Position);
		var delta = new Vector2(_CursorWorld.x - startWorld.x, _CursorWorld.y - startWorld.y);

		if (delta.Length < 1.0f)
			return false;

		float step = MathF.Max(1.0f, AngleStep);
		float raw = MathF.Atan2(delta.y, delta.x).RadianToDegree();
		float best = SnapTo(raw, 0.0f, step);

		foreach (var segment in _Network.GetSegmentsAt(last.Id))
		{
			if (!_Network.TryGetCurve(segment, out var curve))
				continue;

			// The road's direction leaving this node, and straight on through it.
			Vector3 tangent = segment.StartNodeId == last.Id ? curve.Tangent(0.0f) : -curve.Tangent(1.0f);
			Vector3 worldTangent = transform.NormalToWorld(tangent);
			var flat = new Vector2(worldTangent.x, worldTangent.y);

			if (flat.Length < 0.001f)
				continue;

			float reference = MathF.Atan2(flat.y, flat.x).RadianToDegree();

			foreach (float candidate in new[] { SnapTo(raw, reference, step), SnapTo(raw, reference + 180.0f, step) })
			{
				if (MathF.Abs(AngleDifference(candidate, raw)) < MathF.Abs(AngleDifference(best, raw)))
					best = candidate;
			}
		}

		var direction = new Vector2(MathF.Cos(best.DegreeToRadian()), MathF.Sin(best.DegreeToRadian()));
		var start = new Vector2(startWorld.x, startWorld.y);
		float length = MathF.Max(1.0f, Vector2.Dot(delta, direction));
		float tolerance = AlignTolerance(_CursorWorld);

		_Angle = NormalizeAngle(best);

		// A road the snapped line crosses near the end splits there: the merge comes out dead straight. The road
		// under the cursor is what the user is aiming at, so it takes the crossing from much further off.
		if (TryCrossRoad(_Network, start, direction, length, tolerance, _Hovered, out RoadSegmentPick pick, out Vector3 crossingWorld))
		{
			_SnappedWorld = crossingWorld;
			_Split = pick;

			// Lining up with a node as well is only a coincidence here; the guide would mislead.
			m_AlignAnchor = null;

			return true;
		}

		if (TryAlign(_Network, last, start, direction, length, tolerance, out float aligned, out Vector3 anchor))
		{
			length = aligned;
			m_AlignAnchor = anchor;
		}

		Vector2 snapped = start + direction * length;

		_SnappedWorld = new Vector3(snapped.x, snapped.y, GroundHeightAt(snapped, _CursorWorld.z));

		return true;
	}



	/// <summary>
	/// Where the snapped line crosses a road's centreline, when that crossing is within the tolerance of the
	/// cursor's distance along the line, or, for the road under the cursor, within a couple of road widths of it.
	/// The nearest such crossing wins.
	/// </summary>
	private static bool TryCrossRoad(RoadNetwork _Network, Vector2 _Start, Vector2 _Direction, float _Length, float _Tolerance, RoadSegmentPick? _Hovered, out RoadSegmentPick _Pick, out Vector3 _World)
	{
		var transform = _Network.WorldTransform;
		float bestError = float.MaxValue;
		bool found = false;

		_Pick = default;
		_World = default;

		foreach (var segment in _Network.Segments)
		{
			var table = _Network.GetCurveTable(segment);

			if (table is null)
				continue;

			bool hovered = _Hovered is { } hover && hover.Segment.Id == segment.Id;
			float allowed = hovered ? MathF.Max(_Tolerance, segment.Width * 2.0f) : _Tolerance;
			Vector3 previousWorld = transform.PointToWorld(table.SamplePosition(0));

			for (int i = 1; i < table.SampleCount; i++)
			{
				Vector3 currentWorld = transform.PointToWorld(table.SamplePosition(i));
				var p = new Vector2(previousWorld.x, previousWorld.y);
				var edge = new Vector2(currentWorld.x - previousWorld.x, currentWorld.y - previousWorld.y);
				float denominator = Cross(_Direction, edge);

				if (MathF.Abs(denominator) > 0.000001f)
				{
					Vector2 offset = p - _Start;
					float t = Cross(offset, edge) / denominator;
					float u = Cross(offset, _Direction) / denominator;

					// Past the start of the line and within this piece of the road.
					if (t >= 1.0f && u >= 0.0f && u <= 1.0f)
					{
						float error = MathF.Abs(t - _Length);

						if (error <= allowed && error < bestError)
						{
							float distance = table.SampleDistance(i - 1) + u * (table.SampleDistance(i) - table.SampleDistance(i - 1));
							float curveT = table.DistanceToT(distance);

							bestError = error;
							found = true;
							_Pick = new RoadSegmentPick(segment, curveT, table.Curve.Evaluate(curveT));
							_World = Vector3.Lerp(previousWorld, currentWorld, u);
						}
					}
				}

				previousWorld = currentWorld;
			}
		}

		return found;
	}



	/// <summary>
	/// With the direction fixed, the distance along it is the one thing left to place; this snaps it to where the
	/// road's line crosses a guide line through another node, when the cursor is close to that crossing. Guides
	/// through a node run square to the road being drawn (the end lines up level with that node), along the world
	/// axes, and along the roads leaving that node (the end lands on that road's line). Grids come out exact.
	/// </summary>
	private bool TryAlign(RoadNetwork _Network, RoadNode _Last, Vector2 _Start, Vector2 _Direction, float _Length, float _Tolerance, out float _AlignedLength, out Vector3 _AnchorWorld)
	{
		var transform = _Network.WorldTransform;
		var perpendicular = new Vector2(-_Direction.y, _Direction.x);
		float bestError = _Tolerance;
		bool found = false;

		_AlignedLength = _Length;
		_AnchorWorld = default;

		foreach (var node in _Network.Nodes)
		{
			if (node.Id == _Last.Id)
				continue;

			Vector3 world = transform.PointToWorld(node.Position);
			var anchor = new Vector2(world.x, world.y);

			foreach (Vector2 guide in GuidesAt(_Network, node, perpendicular))
			{
				float denominator = Cross(_Direction, guide);

				// A guide nearly along the road crosses it nowhere useful.
				if (MathF.Abs(denominator) < 0.05f)
					continue;

				float crossing = Cross(anchor - _Start, guide) / denominator;

				if (crossing < 1.0f)
					continue;

				float error = MathF.Abs(crossing - _Length);

				if (error >= bestError)
					continue;

				bestError = error;
				_AlignedLength = crossing;
				_AnchorWorld = world;
				found = true;
			}
		}

		return found;
	}



	private static IEnumerable<Vector2> GuidesAt(RoadNetwork _Network, RoadNode _Node, Vector2 _Perpendicular)
	{
		yield return _Perpendicular;
		yield return new Vector2(1.0f, 0.0f);
		yield return new Vector2(0.0f, 1.0f);

		var transform = _Network.WorldTransform;

		foreach (var segment in _Network.GetSegmentsAt(_Node.Id))
		{
			if (!_Network.TryGetCurve(segment, out var curve))
				continue;

			Vector3 tangent = segment.StartNodeId == _Node.Id ? curve.Tangent(0.0f) : -curve.Tangent(1.0f);
			Vector3 worldTangent = transform.NormalToWorld(tangent);
			var flat = new Vector2(worldTangent.x, worldTangent.y);

			if (flat.Length > 0.001f)
				yield return flat.Normal;
		}
	}



	/// <summary>
	/// How close (in the world, at the cursor) the road's end has to be to a guide crossing to take it: a constant
	/// on-screen distance, like the node handles.
	/// </summary>
	private static float AlignTolerance(Vector3 _World)
	{
		return MathF.Max(25.0f, Gizmo.Camera.Position.Distance(_World) * 0.025f);
	}



	private static float Cross(Vector2 _A, Vector2 _B)
	{
		return _A.x * _B.y - _A.y * _B.x;
	}



	/// <summary>
	/// The multiple of the step, counted from the reference angle, nearest to the angle.
	/// </summary>
	private static float SnapTo(float _Angle, float _Reference, float _Step)
	{
		return _Reference + MathF.Round(AngleDifference(_Angle, _Reference) / _Step) * _Step;
	}



	/// <summary>
	/// The signed shortest turn from one angle to the other, in (-180, 180].
	/// </summary>
	private static float AngleDifference(float _To, float _From)
	{
		float difference = (_To - _From) % 360.0f;

		if (difference > 180.0f)
			difference -= 360.0f;
		else if (difference <= -180.0f)
			difference += 360.0f;

		return difference;
	}



	private static float NormalizeAngle(float _Angle)
	{
		float normalized = _Angle % 360.0f;

		return normalized < 0.0f ? normalized + 360.0f : normalized;
	}



	/// <summary>
	/// The ground height at a point in plan, from a trace straight down through it (roads skipped, like the cursor
	/// trace); the fallback height when there is nothing there.
	/// </summary>
	private float GroundHeightAt(Vector2 _Point, float _Fallback)
	{
		const float reach = 20000.0f;

		var top = new Vector3(_Point.x, _Point.y, _Fallback + reach * 0.5f);
		var ray = new Ray(top, Vector3.Down);

		var physics = Scene.Trace.Ray(ray, reach).WithoutTags(RoadNetwork.ChunkTag).Run();

		if (physics.Hit)
			return physics.EndPosition.z;

		var meshes = Scene.Trace.Ray(ray, reach).UseRenderMeshes(true, EditorPreferences.BackfaceSelection).WithoutTags("hidden").WithoutTags(RoadNetwork.ChunkTag).UsePhysicsWorld(false).Run();

		return meshes.Hit ? meshes.EndPosition.z : _Fallback;
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
			{
				// A sharp corner needs both ends of the new road to hold the straight handles the road starts with,
				// so the smoothing must not touch either node: the one being extended from (it was placed
				// with smoothing on if the toggle changed mid-chain) any more than the new one. Handles the nodes
				// already have are left alone, so a road already running through is not kinked.
				if (SharpCorners)
				{
					network.SetAutoTangents(m_LastNodeId, false, false);
					network.SetAutoTangents(node.Id, false, false);
				}

				network.AddSegment(m_LastNodeId, node.Id, Width, Profile, Lanes, OneWay ? Lanes : RoadSegment.DefaultForwardLanes(Lanes), Bridge, Props);
			}

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
					RoadGizmos.DrawRoadOutline(roadCurve, _Network.WidthOf(pick.Segment), _Network.WorldTransform);

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

			if (m_SnappedAngle is { } angle && _Network.FindNode(m_LastNodeId) is { } last)
			{
				// The snapped direction, drawn out past the target, with the angle it landed on.
				Vector3 start = transform.PointToWorld(last.Position);
				var direction = new Vector3(MathF.Cos(angle.DegreeToRadian()), MathF.Sin(angle.DegreeToRadian()), 0.0f);

				Gizmo.Draw.LineThickness = 1.0f;
				Gizmo.Draw.Color = Color.Cyan.WithAlpha(0.5f);
				Gizmo.Draw.Line(start, start + direction * GuideLength);

				Gizmo.Draw.Color = Color.Cyan;
				Gizmo.Draw.ScreenText($"{angle:0}°", _Target.World, new Vector2(12.0f, -12.0f), size: 12.0f);
			}

			if (m_AlignAnchor is { } anchor)
			{
				// The node the end is lined up with, and the line between them.
				Gizmo.Draw.LineThickness = 2.0f;
				Gizmo.Draw.Color = Color.Orange;
				DrawDashed(anchor, _Target.World);
				RoadGizmos.DrawNode(anchor, true);
			}
		}
	}



	private static void DrawDashed(Vector3 _From, Vector3 _To)
	{
		float length = Vector3.DistanceBetween(_From, _To);

		if (length < 0.01f)
			return;

		// Dashes keep a constant on-screen size.
		float dash = MathF.Max(10.0f, Gizmo.Camera.Position.Distance(_To) * 0.02f);
		Vector3 direction = (_To - _From) / length;

		for (float at = 0.0f; at < length; at += dash * 2.0f)
		{
			float end = MathF.Min(at + dash, length);
			Gizmo.Draw.Line(_From + direction * at, _From + direction * end);
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

		SidebarRows.AddWide(group, serialized.GetProperty(nameof(RoadDrawTool.Profile)));
		SidebarRows.AddWide(group, serialized.GetProperty(nameof(RoadDrawTool.Bridge)));
		SidebarRows.AddWide(group, serialized.GetProperty(nameof(RoadDrawTool.Props)));
		SidebarRows.Add(group, serialized.GetProperty(nameof(RoadDrawTool.Width)));
		SidebarRows.Add(group, serialized.GetProperty(nameof(RoadDrawTool.Lanes)));
		SidebarRows.Add(group, serialized.GetProperty(nameof(RoadDrawTool.OneWay)));
		SidebarRows.Add(group, serialized.GetProperty(nameof(RoadDrawTool.SnapRadius)));
		SidebarRows.Add(group, serialized.GetProperty(nameof(RoadDrawTool.SharpCorners)));

		var snapping = AddGroup("Snapping");

		SidebarRows.Add(snapping, serialized.GetProperty(nameof(RoadDrawTool.AngleSnap)));
		SidebarRows.Add(snapping, serialized.GetProperty(nameof(RoadDrawTool.AngleStep)));

		AddShortcuts(
			("Place Node", "LMB"),
			("Continue From / Join Node", "LMB on node"),
			("Branch From / Merge Into Road", "LMB on road"),
			("Snap Angle + Align With Nodes", "Hold SHIFT"),
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
