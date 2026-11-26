using System.Collections.Generic;
using UnityEngine;

namespace Pathologic2ProjectTransparency
{
    // Mode = Fade: a better drawing order for see-through objects than Unity's "far first by the center of the bounds".
    // Without depth any fixed order is wrong somewhere, so this is a compromise that targets the cases that broke:
    // - a room (walls and floor are one big mesh) has its center next to the camera, so it was drawn last and covered
    //   everything inside it -> houses and rooms (bounds over 12 m) are drawn first, biggest first;
    // - a branch lying on a table vanished under the table, depending on where you stood (the centers swap order as you
    //   move) -> near objects are drawn bottom first, by the lowest point of their bounds: the floor before the table,
    //   the table before the branch on it, a shelf before the jars on it;
    // - the fringe and parts of a shirt vanished under the head and the body -> characters (anything under an Animator)
    //   are placed by the height of their feet, and within a character bigger parts are drawn first.
    // Objects farther than NearRadius keep order 0 and are sorted by distance; near ones get higher orders, so they are
    // drawn after them (back to front, as it should be). Orders are recomputed on every scan, relative to the camera height.
    // Implemented with Renderer.sortingOrder (lower = drawn first). The original orders are put back on restore.
    internal sealed class FadeSorting
    {
        private const float BigSize = 12f;
        private const float NearRadius = 40f;
        private const float HeightStep = 0.2f;
        private const int NearBase = 10000;

        private sealed class Entry
        {
            public Renderer Renderer;
            public int OriginalOrder;
            public Animator Character;
            public int PartRank;
        }

        private readonly Dictionary<int, Entry> tracked = new Dictionary<int, Entry>();
        private readonly HashSet<int> seen = new HashSet<int>();
        private readonly List<Material> materials = new List<Material>();

        public int Count => tracked.Count;

        // About one step per 19% of size (4 steps per doubling).
        private static int SizeSteps(float size)
        {
            return Mathf.Clamp(Mathf.RoundToInt(Mathf.Log(Mathf.Max(size, 0.01f), 2f) * 4f), -100, 100);
        }

        // Finds renderers that use a converted material (once per renderer). isConverted: by material instance id.
        public int Discover(System.Func<int, bool> isConverted)
        {
            int added = 0;
            foreach (Renderer r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (r == null || !seen.Add(r.GetInstanceID()))
                {
                    continue;
                }
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer))
                {
                    continue;
                }
                r.GetSharedMaterials(materials);
                bool uses = false;
                foreach (Material m in materials)
                {
                    if (m != null && isConverted(m.GetInstanceID()))
                    {
                        uses = true;
                        break;
                    }
                }
                if (!uses)
                {
                    continue;
                }
                var e = new Entry { Renderer = r, OriginalOrder = r.sortingOrder, Character = r.GetComponentInParent<Animator>() };
                if (e.Character != null)
                {
                    // 1 for a 2 m body, more for smaller parts (shirt, head, hair), up to 9.
                    e.PartRank = Mathf.Clamp((8 - SizeSteps(r.bounds.size.magnitude)) / 2, 1, 9);
                }
                tracked[r.GetInstanceID()] = e;
                added++;
            }
            return added;
        }

        public void Update(Camera camera)
        {
            if (camera == null)
            {
                return;
            }
            Vector3 eye = camera.transform.position;
            float nearSqr = NearRadius * NearRadius;
            foreach (Entry e in tracked.Values)
            {
                Renderer r = e.Renderer;
                if (r == null)
                {
                    continue;
                }
                Bounds b = r.bounds;
                float size = b.size.magnitude;
                int order;
                if (size > BigSize)
                {
                    order = -1 - (SizeSteps(size) - SizeSteps(BigSize)); // -1 for 12 m, lower for bigger
                }
                else if (b.SqrDistance(eye) > nearSqr)
                {
                    order = 0;
                }
                else if (e.Character != null)
                {
                    float feet = e.Character.transform.position.y;
                    order = NearBase + Mathf.Clamp(Mathf.RoundToInt((feet - eye.y) / HeightStep), -900, 900) * 10 + e.PartRank;
                }
                else
                {
                    order = NearBase + Mathf.Clamp(Mathf.RoundToInt((b.min.y - eye.y) / HeightStep), -900, 900) * 10;
                }
                if (r.sortingOrder != order)
                {
                    r.sortingOrder = order;
                }
            }
        }

        public void Restore()
        {
            foreach (Entry e in tracked.Values)
            {
                if (e.Renderer != null)
                {
                    e.Renderer.sortingOrder = e.OriginalOrder;
                }
            }
            tracked.Clear();
            seen.Clear();
        }
    }
}
