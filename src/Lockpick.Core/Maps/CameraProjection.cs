namespace Lockpick.Core.Maps;

/// <summary>
/// Where a point in a room shows in the game's picture, for one of the room's fixed cameras: a pinhole camera looking
/// from <see cref="MapCamera"/>'s position at its target, with the field-of-view value as the focal length in the
/// picture's 320×240 units (the PlayStation-era frame the PC version scales up). The basis follows
/// ecruells/resident-evil-pc-decomp's collision overlay (forward n = target − position, right r = (dz, 0, −dx), up
/// u = n × r; the room's −Y is up). Checked live 2026-10-27: the player's feet projected onto them in room 115.
/// </summary>
public static class CameraProjection
{
    public const double PictureWidth = 320;
    public const double PictureHeight = 240;

    /// <summary>Points nearer the camera than this (room units) aren't drawn.</summary>
    public const double Near = 64;

    /// <summary>The point in picture units (0–320, 0–240, may lie outside), or null when it is behind the camera.</summary>
    public static (double X, double Y)? Project(MapCamera camera, double x, double y, double z)
    {
        if (camera.Fov <= 0)
        {
            return null;
        }

        double dx = camera.TargetX - camera.X, dy = camera.TargetY - camera.Y, dz = camera.TargetZ - camera.Z;
        var length = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        var flat = Math.Sqrt((dx * dx) + (dz * dz));
        if (length < 1 || flat < 1)
        {
            return null;
        }

        double nx = dx / length, ny = dy / length, nz = dz / length;
        double rx = dz / flat, rz = -dx / flat;
        double ux = (ny * rz) - (nz * 0), uy = (nz * rx) - (nx * rz), uz = (nx * 0) - (ny * rx);
        double px = x - camera.X, py = y - camera.Y, pz = z - camera.Z;
        var depth = (nx * px) + (ny * py) + (nz * pz);
        if (depth < Near)
        {
            return null;
        }

        var scale = camera.Fov / depth;
        return ((PictureWidth / 2) + (((rx * px) + (rz * pz)) * scale), (PictureHeight / 2) + (((ux * px) + (uy * py) + (uz * pz)) * scale));
    }
}
