// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Godot.UI;

internal sealed partial class EarthSiteMap : Control
{
    private readonly Texture2D? _texture;
    private EarthPlaceCatalogue _places = EarthPlaceCatalogue.Empty;
    private EarthSite _site;
    private Vector2 _center;
    private Vector2? _pressed;
    private bool _dragged;
    private float _zoom = 1;
    private float PixelsPerDegree => Math.Min(Size.X / 360, Size.Y / 180) * _zoom;
    internal event Action<EarthPlace?, EarthSite>? Picked;

    internal EarthSiteMap(Texture2D? texture)
    {
        _texture = texture;
        Name = "EarthSiteMap";
        CustomMinimumSize = new(0, 152);
        ClipContents = true;
        MouseFilter = MouseFilterEnum.Stop;
    }

    internal void Update(EarthPlaceCatalogue places, EarthSite site)
    {
        if (ReferenceEquals(places, _places) && site == _site)
            return;
        _places = places;
        _site = site;
        QueueRedraw();
    }

    internal Vector2 Project(double latitude, double longitude) =>
        Size / 2 + new Vector2((float)longitude - _center.X, _center.Y - (float)latitude) * PixelsPerDegree;

    private EarthSite Unproject(Vector2 point)
    {
        Vector2 offset = (point - Size / 2) / PixelsPerDegree;
        return new(Math.Clamp(_center.Y - offset.Y, -90, 90), Math.Clamp(_center.X + offset.X, -180, 180), 0);
    }

    private void Constrain()
    {
        float halfWidth = Size.X / (2 * PixelsPerDegree);
        float halfHeight = Size.Y / (2 * PixelsPerDegree);
        float longitude = Math.Max(0, 180 - halfWidth), latitude = Math.Max(0, 90 - halfHeight);
        _center = new(Math.Clamp(_center.X, -longitude, longitude), Math.Clamp(_center.Y, -latitude, latitude));
    }

    public override void _GuiInput(InputEvent input)
    {
        if (!(PixelsPerDegree > 0))
            return;
        switch (input)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel:
                var site = Unproject(wheel.Position);
                _zoom = Math.Clamp(_zoom * (wheel.ButtonIndex == MouseButton.WheelUp ? 1.25f : .8f), 1, 64);
                Vector2 offset = (wheel.Position - Size / 2) / PixelsPerDegree;
                _center = new((float)site.LongitudeDegrees - offset.X, (float)site.LatitudeDegrees + offset.Y);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } button:
                if (button.Pressed)
                {
                    _pressed = button.Position;
                    _dragged = false;
                }
                else if (_pressed.HasValue)
                {
                    _pressed = null;
                    if (!_dragged)
                        Pick(button.Position);
                }
                break;
            case InputEventMouseMotion motion when _pressed is { } start && (motion.ButtonMask & MouseButtonMask.Left) != 0:
                if (!_dragged && motion.Position.DistanceTo(start) < 4)
                    return;
                _dragged = true;
                _center -= new Vector2(motion.Relative.X, -motion.Relative.Y) / PixelsPerDegree;
                break;
            default:
                return;
        }
        Constrain();
        QueueRedraw();
        AcceptEvent();
    }

    private void Pick(Vector2 point)
    {
        Vector2 top = Project(90, -180), bottom = Project(-90, 180);
        if (!new Rect2(top, bottom - top).HasPoint(point))
            return;
        EarthPlace? selected = null;
        float nearest = 7;
        foreach (EarthPlace place in _places.Places.Where(VisiblePlace))
        {
            float distance = Project(place.LatitudeDegrees, place.LongitudeDegrees).DistanceTo(point);
            if (distance >= nearest)
                continue;
            selected = place;
            nearest = distance;
        }
        Picked?.Invoke(selected, selected?.ObserverSite ?? Unproject(point));
    }

    public override void _Draw()
    {
        if (!(PixelsPerDegree > 0))
            return;
        Constrain();
        Vector2 top = Project(90, -180), bottom = Project(-90, 180);
        DrawRect(new(Vector2.Zero, Size), new Color("182630"));
        if (_texture is not null)
        {
            var target = new Rect2(top, bottom - top);
            var visible = target.Intersection(new(Vector2.Zero, Size));
            if (visible.HasArea())
            {
                Vector2 scale = _texture.GetSize() / target.Size;
                DrawTextureRectRegion(_texture, visible, new((visible.Position - top) * scale, visible.Size * scale));
            }
        }
        Color grid = new(1, 1, 1, .18f);
        for (int longitude = -150; longitude < 180; longitude += 30)
            DrawLine(Project(90, longitude), Project(-90, longitude), grid);
        for (int latitude = -60; latitude <= 60; latitude += 30)
            DrawLine(Project(latitude, -180), Project(latitude, 180), grid);
        var bounds = new Rect2(Vector2.Zero, Size).Grow(6);
        foreach (EarthPlace place in _places.Places.Where(VisiblePlace))
        {
            Vector2 position = Project(place.LatitudeDegrees, place.LongitudeDegrees);
            if (bounds.HasPoint(position))
                DrawCircle(position, place.Kind == 'U' ? 3 : 1.5f, place.Kind == 'U' ? Colors.Gold : new(1, 1, 1, .7f));
        }
        Vector2 here = Project(_site.LatitudeDegrees, _site.LongitudeDegrees);
        DrawArc(here, 5, 0, Mathf.Tau, 20, new Color("ff7966"), 2, true);
    }

    private bool VisiblePlace(EarthPlace place) => place.Kind != 'C' || place.PopulationThousands >= 2000 / (_zoom * _zoom);
}
