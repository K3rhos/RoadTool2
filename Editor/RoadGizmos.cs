using Sandbox;

namespace RedSnail.RoadTool2.Editor;

/// <summary>
/// Gizmo drawing shared by the road tools. Curves are in network-local space and drawn through the network's
/// transform, in whatever colour and thickness the caller set up.
/// </summary>
internal static class RoadGizmos
{
	public static void DrawCurve(in RoadCurve _Curve, in Transform _Transform, int _Steps = 24)
	{
		Vector3 previous = _Transform.PointToWorld(_Curve.P0);

		for (int i = 1; i <= _Steps; i++)
		{
			Vector3 point = _Transform.PointToWorld(_Curve.Evaluate((float)i / _Steps));

			Gizmo.Draw.Line(previous, point);
			previous = point;
		}
	}



	/// <summary>
	/// Draws the two edges of a road of the given width along the curve, capped at both ends.
	/// </summary>
	public static void DrawRoadOutline(in RoadCurve _Curve, float _Width, in Transform _Transform, int _Steps = 24)
	{
		float halfWidth = _Width * 0.5f;
		Vector3 previousLeft = default;
		Vector3 previousRight = default;

		for (int i = 0; i <= _Steps; i++)
		{
			float t = (float)i / _Steps;
			Vector3 point = _Curve.Evaluate(t);
			Vector3 right = RoadCurve.RightOf(_Curve.Tangent(t));

			Vector3 left = _Transform.PointToWorld(point - right * halfWidth);
			Vector3 rightEdge = _Transform.PointToWorld(point + right * halfWidth);

			if (i == 0 || i == _Steps)
				Gizmo.Draw.Line(left, rightEdge);

			if (i > 0)
			{
				Gizmo.Draw.Line(previousLeft, left);
				Gizmo.Draw.Line(previousRight, rightEdge);
			}

			previousLeft = left;
			previousRight = rightEdge;
		}
	}



	/// <summary>
	/// A node marker: a flat ring on the ground, filled when it is the one being worked on.
	/// </summary>
	public static void DrawNode(Vector3 _WorldPosition, bool _Filled)
	{
		float radius = RoadPicker.NodeRadius(_WorldPosition);

		if (_Filled)
			Gizmo.Draw.SolidSphere(_WorldPosition, radius * 0.6f);

		Gizmo.Draw.LineCircle(_WorldPosition, Vector3.Up, radius);
	}
}
