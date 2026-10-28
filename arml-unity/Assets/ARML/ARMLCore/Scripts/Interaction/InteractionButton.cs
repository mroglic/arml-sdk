using System;
using UnityEngine;

namespace ARML.Interaction
{
    /// <summary>
    /// The lantern's interaction button. By default it is the left mouse button / any touch; a platform can
    /// replace it (the iPhone rig uses an on-screen "Interakcija" button, so other UI taps do not interact).
    /// </summary>
    public static class InteractionButton
    {
        /// <summary>Overrides for pressed this frame, held, and released this frame; null keeps the mouse.</summary>
        public static Func<bool> Down, Held, Up;

        public static bool GetDown() => Down?.Invoke() ?? Input.GetMouseButtonDown(0);
        public static bool Get() => Held?.Invoke() ?? Input.GetMouseButton(0);
        public static bool GetUp() => Up?.Invoke() ?? Input.GetMouseButtonUp(0);
    }
}
