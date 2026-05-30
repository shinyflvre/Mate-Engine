using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
using System.Runtime.InteropServices;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
#elif ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Kirurobo
{
    public class UniWindowMoveHandle : MonoBehaviour, IDragHandler, IBeginDragHandler, IEndDragHandler, IPointerUpHandler
    {
        private UniWindowController _uniwinc;
        public bool disableOnZoomed = true;

        [Range(0f, 100f)] public float dragSmooth = 0f;

        public bool IsDragging => _isDragging;
        private bool _isDragging = false;

        private bool IsEnabled => enabled && (!disableOnZoomed || !IsZoomed);
        private bool IsZoomed => (_uniwinc && (_uniwinc.shouldFitMonitor || _uniwinc.isZoomed));

        private bool _isHitTestEnabled;
        private Vector2 _grabOffset;

        private Animator _avatarAnimator;
        private bool _hasSitParam;
        private static readonly int IsWindowSitHash = Animator.StringToHash("isWindowSit");

        private Vector2 _dragTarget;
        private Vector2 _dragVel;
        private const float MaxSmoothTime = 0.35f;

        void Start()
        {
            _uniwinc = GameObject.FindAnyObjectByType<UniWindowController>();
            if (_uniwinc) _isHitTestEnabled = _uniwinc.isHitTestEnabled;
            RefreshAnimator();
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (!IsEnabled) return;
            RefreshAnimator();
            _grabOffset = _uniwinc.windowPosition - _uniwinc.cursorPosition;
            if (!_isDragging)
            {
                _isHitTestEnabled = _uniwinc.isHitTestEnabled;
                _uniwinc.isHitTestEnabled = false;
                _uniwinc.isClickThrough = false;
            }
            _isDragging = true;
            _dragVel = Vector2.zero;
            _dragTarget = _uniwinc.windowPosition;
        }

        public void OnEndDrag(PointerEventData eventData) { EndDragging(); }
        public void OnPointerUp(PointerEventData eventData) { EndDragging(); }

        private void EndDragging()
        {
            if (_isDragging && _uniwinc) _uniwinc.isHitTestEnabled = _isHitTestEnabled;
            _isDragging = false;
            _dragVel = Vector2.zero;
        }

#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
        void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus) EndDragging();
        }

        void OnDisable()
        {
            EndDragging();
        }
#endif

        public void OnDrag(PointerEventData eventData)
        {
            if (!_uniwinc || !_isDragging) return;
            if (!IsEnabled) { EndDragging(); return; }
            if (eventData.button != PointerEventData.InputButton.Left) return;
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)
                || Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)
                || Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) return;
#elif ENABLE_INPUT_SYSTEM
            if (Keyboard.current[Key.LeftShift].isPressed || Keyboard.current[Key.RightShift].isPressed
                || Keyboard.current[Key.LeftCtrl].isPressed || Keyboard.current[Key.RightCtrl].isPressed
                || Keyboard.current[Key.LeftAlt].isPressed || Keyboard.current[Key.RightAlt].isPressed) return;
#endif
#if !UNITY_EDITOR
            if (Screen.fullScreen) { EndDragging(); return; }
#endif
            if (_avatarAnimator == null || !_hasSitParam) RefreshAnimator();

            bool lockY = _avatarAnimator && _hasSitParam && _avatarAnimator.GetBool(IsWindowSitHash);
            Vector2 next = _uniwinc.cursorPosition + _grabOffset;
            if (lockY) next.y = _uniwinc.windowPosition.y;
            _dragTarget = next;
        }

        void Update()
        {
            if (!_uniwinc) return;
            if (!_isDragging) return;
#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
            if (!IsPrimaryPointerPressed())
            {
                EndDragging();
                return;
            }
#endif
            float t = Mathf.Clamp01(dragSmooth * 0.01f) * MaxSmoothTime;
            if (t <= 0f) _uniwinc.windowPosition = _dragTarget;
            else _uniwinc.windowPosition = Vector2.SmoothDamp(_uniwinc.windowPosition, _dragTarget, ref _dragVel, t);
        }

#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
        private static bool IsPrimaryPointerPressed()
        {
            try { return MacNative.IsLeftMouseButtonPressed(); }
            catch { }
            return IsUnityPrimaryPointerPressed();
        }

        private static bool IsUnityPrimaryPointerPressed()
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetMouseButton(0);
#elif ENABLE_INPUT_SYSTEM
            return Mouse.current != null && Mouse.current.leftButton.isPressed;
#else
            return true;
#endif
        }

        private static class MacNative
        {
            [DllImport("MateDesktopWindowMac", EntryPoint = "MateDWIsLeftMouseButtonPressed")]
            [return: MarshalAs(UnmanagedType.I1)]
            public static extern bool IsLeftMouseButtonPressed();
        }
#endif

        private void RefreshAnimator()
        {
            Animator best = GetComponentInParent<Animator>();
            if (best == null || !HasParam(best, IsWindowSitHash))
            {
                var all = GameObject.FindObjectsOfType<Animator>();
                for (int i = 0; i < all.Length; i++)
                {
                    var a = all[i];
                    if (HasParam(a, IsWindowSitHash) && a.GetBool(IsWindowSitHash)) { best = a; break; }
                }
                if (best == null)
                {
                    for (int i = 0; i < all.Length; i++)
                    {
                        var a = all[i];
                        if (HasParam(a, IsWindowSitHash)) { best = a; break; }
                    }
                }
                if (best == null && all.Length > 0) best = all[0];
            }
            _avatarAnimator = best;
            _hasSitParam = _avatarAnimator && HasParam(_avatarAnimator, IsWindowSitHash);
        }

        private static bool HasParam(Animator a, int hash)
        {
            if (!a) return false;
            var ps = a.parameters;
            for (int i = 0; i < ps.Length; i++) if (ps[i].nameHash == hash) return true;
            return false;
        }
    }
}



/*
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
#if ENABLE_LEGACY_INPUT_MANAGER
#elif ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Kirurobo
{
    public class UniWindowMoveHandle : MonoBehaviour, IDragHandler, IBeginDragHandler, IEndDragHandler, IPointerUpHandler
    {
        private UniWindowController _uniwinc;
        public bool disableOnZoomed = true;

        public bool IsDragging => _isDragging;
        private bool _isDragging = false;

        private bool IsEnabled => enabled && (!disableOnZoomed || !IsZoomed);
        private bool IsZoomed => (_uniwinc && (_uniwinc.shouldFitMonitor || _uniwinc.isZoomed));

        private bool _isHitTestEnabled;
        private Vector2 _grabOffset;

        private Animator _avatarAnimator;
        private bool _hasSitParam;
        private static readonly int IsWindowSitHash = Animator.StringToHash("isWindowSit");

        void Start()
        {
            _uniwinc = GameObject.FindAnyObjectByType<UniWindowController>();
            if (_uniwinc) _isHitTestEnabled = _uniwinc.isHitTestEnabled;
            RefreshAnimator();
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (!IsEnabled) return;
            RefreshAnimator();
            _grabOffset = _uniwinc.windowPosition - _uniwinc.cursorPosition;
            if (!_isDragging)
            {
                _isHitTestEnabled = _uniwinc.isHitTestEnabled;
                _uniwinc.isHitTestEnabled = false;
                _uniwinc.isClickThrough = false;
            }
            _isDragging = true;
        }

        public void OnEndDrag(PointerEventData eventData) { EndDragging(); }
        public void OnPointerUp(PointerEventData eventData) { EndDragging(); }

        private void EndDragging()
        {
            if (_isDragging) _uniwinc.isHitTestEnabled = _isHitTestEnabled;
            _isDragging = false;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!_uniwinc || !_isDragging) return;
            if (!IsEnabled) { EndDragging(); return; }
            if (eventData.button != PointerEventData.InputButton.Left) return;
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)
                || Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)
                || Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) return;
#elif ENABLE_INPUT_SYSTEM
            if (Keyboard.current[Key.LeftShift].isPressed || Keyboard.current[Key.RightShift].isPressed
                || Keyboard.current[Key.LeftCtrl].isPressed || Keyboard.current[Key.RightCtrl].isPressed
                || Keyboard.current[Key.LeftAlt].isPressed || Keyboard.current[Key.RightAlt].isPressed) return;
#endif
#if !UNITY_EDITOR
            if (Screen.fullScreen) { EndDragging(); return; }
#endif
            if (_avatarAnimator == null || !_hasSitParam) RefreshAnimator();

            bool lockY = _avatarAnimator && _hasSitParam && _avatarAnimator.GetBool(IsWindowSitHash);
            Vector2 next = _uniwinc.cursorPosition + _grabOffset;
            if (lockY) _uniwinc.windowPosition = new Vector2(next.x, _uniwinc.windowPosition.y);
            else _uniwinc.windowPosition = next;
        }

        private void RefreshAnimator()
        {
            Animator best = GetComponentInParent<Animator>();
            if (best == null || !HasParam(best, IsWindowSitHash))
            {
                var all = GameObject.FindObjectsOfType<Animator>();
                for (int i = 0; i < all.Length; i++)
                {
                    var a = all[i];
                    if (HasParam(a, IsWindowSitHash) && a.GetBool(IsWindowSitHash)) { best = a; break; }
                }
                if (best == null)
                {
                    for (int i = 0; i < all.Length; i++)
                    {
                        var a = all[i];
                        if (HasParam(a, IsWindowSitHash)) { best = a; break; }
                    }
                }
                if (best == null && all.Length > 0) best = all[0];
            }

            _avatarAnimator = best;
            _hasSitParam = _avatarAnimator && HasParam(_avatarAnimator, IsWindowSitHash);
        }

        private static bool HasParam(Animator a, int hash)
        {
            if (!a) return false;
            var ps = a.parameters;
            for (int i = 0; i < ps.Length; i++) if (ps[i].nameHash == hash) return true;
            return false;
        }
    }
}
*/
