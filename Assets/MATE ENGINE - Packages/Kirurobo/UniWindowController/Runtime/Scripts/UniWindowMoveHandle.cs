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

        [Range(0f, 100f)] public float dragSmooth = 0f;
        public bool restrictDragToAvatarBounds = true;
        [Min(0)] public int dragHitPaddingPx = 16;

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
        private Animator _avatarRendererRoot;
        private Renderer[] _avatarRenderers;
        private float _nextAvatarResolveTime;

        void Start()
        {
            _uniwinc = GameObject.FindAnyObjectByType<UniWindowController>();
            if (_uniwinc) _isHitTestEnabled = _uniwinc.isHitTestEnabled;
            RefreshAnimator();
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (!IsEnabled) return;
            if (!CanBeginWindowDrag(eventData.position)) return;
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
            if (_isDragging) _uniwinc.isHitTestEnabled = _isHitTestEnabled;
            _isDragging = false;
            _dragVel = Vector2.zero;
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

            if (_avatarAnimator && _hasSitParam && _avatarAnimator.GetBool(IsWindowSitHash))
            {
                _dragTarget = _uniwinc.windowPosition;
                _dragVel = Vector2.zero;
                return;
            }

            Vector2 next = _uniwinc.cursorPosition + _grabOffset;
            _dragTarget = next;
        }

        void Update()
        {
            if (!_uniwinc) return;
            if (!_isDragging) return;
            if (IsAvatarWindowSitting())
            {
                _dragTarget = _uniwinc.windowPosition;
                _dragVel = Vector2.zero;
                return;
            }
            float t = Mathf.Clamp01(dragSmooth * 0.01f) * MaxSmoothTime;
            if (t <= 0f) _uniwinc.windowPosition = _dragTarget;
            else _uniwinc.windowPosition = Vector2.SmoothDamp(_uniwinc.windowPosition, _dragTarget, ref _dragVel, t);
        }

        private void RefreshAnimator()
        {
            Animator best = GetComponentInParent<Animator>();
            if (best == null || !HasParam(best, IsWindowSitHash))
            {
                var all = GameObject.FindObjectsByType<Animator>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
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

        private bool IsAvatarWindowSitting()
        {
            if (_avatarAnimator == null || !_hasSitParam) RefreshAnimator();
            return _avatarAnimator && _hasSitParam && _avatarAnimator.GetBool(IsWindowSitHash);
        }

        private static bool HasParam(Animator a, int hash)
        {
            if (!a) return false;
            var ps = a.parameters;
            for (int i = 0; i < ps.Length; i++) if (ps[i].nameHash == hash) return true;
            return false;
        }

        private bool CanBeginWindowDrag(Vector2 screenPosition)
        {
            return !restrictDragToAvatarBounds || IsPointerOverAvatar(screenPosition);
        }

        private bool IsPointerOverAvatar(Vector2 screenPosition)
        {
            Camera cam = _uniwinc && _uniwinc.currentCamera ? _uniwinc.currentCamera : Camera.main;
            if (cam == null) return true;

            ResolveAvatarRenderers();
            if (_avatarRenderers == null || _avatarRenderers.Length == 0) return true;

            bool checkedAnyRenderer = false;
            for (int i = 0; i < _avatarRenderers.Length; i++)
            {
                Renderer r = _avatarRenderers[i];
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (r is ParticleSystemRenderer) continue;

                Bounds bounds = r.bounds;
                if (bounds.extents.sqrMagnitude <= 0.000001f) continue;

                checkedAnyRenderer = true;
                if (ScreenRectContainsRenderer(cam, bounds, screenPosition, dragHitPaddingPx)) return true;
            }

            return !checkedAnyRenderer;
        }

        private void ResolveAvatarRenderers()
        {
            float now = Time.unscaledTime;
            if (_avatarRendererRoot != null && _avatarRendererRoot.isActiveAndEnabled && _avatarRenderers != null && _avatarRenderers.Length > 0 && now < _nextAvatarResolveTime)
                return;

            _nextAvatarResolveTime = now + 1f;
            _avatarRendererRoot = null;

            Animator best = _avatarAnimator;
            if (best == null || !best.isActiveAndEnabled || !best.gameObject.activeInHierarchy)
            {
                var animators = GameObject.FindObjectsByType<Animator>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                for (int i = 0; i < animators.Length; i++)
                {
                    var animator = animators[i];
                    if (animator == null || !animator.isActiveAndEnabled || !animator.gameObject.activeInHierarchy) continue;
                    if (!HasParam(animator, IsWindowSitHash)) continue;
                    best = animator;
                    break;
                }

                if (best == null && animators.Length > 0) best = animators[0];
            }

            _avatarRendererRoot = best;
            _avatarRenderers = _avatarRendererRoot != null
                ? _avatarRendererRoot.GetComponentsInChildren<Renderer>(true)
                : null;
        }

        private static bool ScreenRectContainsRenderer(Camera cam, Bounds bounds, Vector2 screenPosition, float padding)
        {
            Vector3 center = bounds.center;
            Vector3 ext = bounds.extents;
            float minX = float.PositiveInfinity;
            float minY = float.PositiveInfinity;
            float maxX = float.NegativeInfinity;
            float maxY = float.NegativeInfinity;
            bool projected = false;

            for (int x = -1; x <= 1; x += 2)
            {
                for (int y = -1; y <= 1; y += 2)
                {
                    for (int z = -1; z <= 1; z += 2)
                    {
                        Vector3 screen = cam.WorldToScreenPoint(center + Vector3.Scale(ext, new Vector3(x, y, z)));
                        if (screen.z <= 0.001f) continue;

                        projected = true;
                        minX = Mathf.Min(minX, screen.x);
                        minY = Mathf.Min(minY, screen.y);
                        maxX = Mathf.Max(maxX, screen.x);
                        maxY = Mathf.Max(maxY, screen.y);
                    }
                }
            }

            if (!projected) return true;
            return screenPosition.x >= minX - padding &&
                screenPosition.x <= maxX + padding &&
                screenPosition.y >= minY - padding &&
                screenPosition.y <= maxY + padding;
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
