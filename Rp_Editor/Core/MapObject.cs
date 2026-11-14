
using System;
using System.ComponentModel;
using System.Numerics;

namespace ZeusMapEditor.Core;

/// <summary>
/// Representa uma instância de objeto dentro de um arquivo IPL do GTA San Andreas.
/// </summary>
public sealed class MapObject
{
    [Category("Modelo")]
    [DisplayName("ID do modelo")]
    public int Id { get; set; }

    [Category("Modelo")]
    [DisplayName("Nome do modelo")]
    public string ModelName { get; set; } = "";

    [Category("Modelo")]
    [DisplayName("Interior")]
    public int Interior { get; set; }

    [Category("Posição")]
    public float X { get; set; }

    [Category("Posição")]
    public float Y { get; set; }

    [Category("Posição")]
    public float Z { get; set; }

    [Category("Rotação")]
    [DisplayName("Rotação Z (graus)")]
    [Description("Giro em torno do eixo vertical.")]
    public float Yaw
    {
        get
        {
            var q = Orientation;

            double yaw = Math.Atan2(
                2.0 * (q.W * q.Z + q.X * q.Y),
                1.0 - 2.0 * (q.Y * q.Y + q.Z * q.Z)
            );

            return (float)Math.Round(
                yaw * 180.0 / Math.PI,
                3
            );
        }
        set
        {
            float delta = (value - Yaw) * MathF.PI / 180f;

            var rotation = Quaternion.CreateFromAxisAngle(
                Vector3.UnitZ,
                delta
            );

            var actual = Quaternion.Normalize(
                Quaternion.Concatenate(Orientation, rotation)
            );

            var stored = Quaternion.Conjugate(actual);

            RotX = stored.X;
            RotY = stored.Y;
            RotZ = stored.Z;
            RotW = stored.W;
        }
    }

    [Category("Rotação (quaternion do IPL)")]
    public float RotX { get; set; }

    [Category("Rotação (quaternion do IPL)")]
    public float RotY { get; set; }

    [Category("Rotação (quaternion do IPL)")]
    public float RotZ { get; set; }

    [Category("Rotação (quaternion do IPL)")]
    public float RotW { get; set; } = 1f;

    [Category("LOD")]
    [DisplayName("Índice do LOD")]
    public int Lod { get; set; } = -1;

    [Browsable(false)]
    public string TxdName { get; set; } = "";

    [Browsable(false)]
    public string SourceFile { get; set; } = "";

    /// <summary>
    /// Retorna a orientação real do objeto.
    /// O quaternion armazenado pelo IPL é conjugado.
    /// </summary>
    [Browsable(false)]
    public Quaternion Orientation
    {
        get
        {
            var q = new Quaternion(
                RotX,
                RotY,
                RotZ,
                RotW
            );

            if (q.LengthSquared() < 1e-9f)
                return Quaternion.Identity;

            return Quaternion.Normalize(
                Quaternion.Conjugate(q)
            );
        }
    }

    /// <summary>
    /// Cria uma cópia independente dos dados do objeto.
    /// </summary>
    public MapObject Clone()
    {
        return (MapObject)MemberwiseClone();
    }

    public override string ToString()
    {
        return $"{Id} - {ModelName}";
    }
}
