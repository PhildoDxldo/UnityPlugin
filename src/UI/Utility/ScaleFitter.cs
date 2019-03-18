using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace ModIO.UI
{
    #if UNITY_2018_1_OR_NEWER
    [ExecuteAlways]
    #else
    [ExecuteInEditMode]
    #endif
    [RequireComponent(typeof(RectTransform))]
    [DisallowMultipleComponent]
    /// <summary>Scales a RectTransform to fit its parent.</summary>
    public class ScaleFitter : UIBehaviour, ILayoutSelfController
    {
        /// <summary>
        /// Specifies a mode to use to enforce an aspect ratio.
        /// </summary>
        public enum AspectMode
        {
            Disabled,

            /// <summary>
            /// Changes the height of the rectangle to match the aspect ratio.
            /// </summary>
            WidthControlsHeight,
            /// <summary>
            /// Changes the width of the rectangle to match the aspect ratio.
            /// </summary>
            HeightControlsWidth,
            /// <summary>
            /// Sizes the rectangle such that it's fully contained within the parent rectangle.
            /// </summary>
            FitInParent,
            /// <summary>
            /// Sizes the rectangle such that the parent rectangle is fully contained within.
            /// </summary>
            EnvelopeParent,

            StretchIgnoreAspect,
        }

        [SerializeField] private AspectMode m_aspectMode = AspectMode.Disabled;

        /// <summary>
        /// The mode to use to enforce the aspect ratio.
        /// </summary>
        public AspectMode aspectMode
        {
            get { return m_aspectMode; }
            set
            {
                if(m_aspectMode != value)
                {
                    m_aspectMode = value;
                    SetDirty();
                }
            }
        }

        [System.NonSerialized]
        private RectTransform m_Rect;

        // This "delayed" mechanism is required for case 1014834.
        private bool m_DelayedSetDirty = false;

        private RectTransform rectTransform
        {
            get
            {
                if (m_Rect == null)
                    m_Rect = GetComponent<RectTransform>();
                return m_Rect;
            }
        }

        private DrivenRectTransformTracker m_Tracker;

        protected ScaleFitter() {}

        protected override void OnEnable()
        {
            base.OnEnable();
            SetDirty();
        }

        protected override void OnDisable()
        {
            m_Tracker.Clear();
            LayoutRebuilder.MarkLayoutForRebuild(rectTransform);
            base.OnDisable();
        }

        /// <summary>
        /// Update the rect based on the delayed dirty.
        /// Got around issue of calling onValidate from OnEnable function.
        /// </summary>
        protected virtual void Update()
        {
            bool hasParentChanged = (rectTransform.parent != null
                                     && rectTransform.parent.hasChanged);
            if (m_DelayedSetDirty
                || hasParentChanged)
            {
                m_DelayedSetDirty = false;
                SetDirty();
            }
        }

        /// <summary>
        /// Function called when this RectTransform or parent RectTransform has changed dimensions.
        /// </summary>
        protected override void OnRectTransformDimensionsChange()
        {
            UpdateRect();
        }

        protected override void OnTransformParentChanged()
        {
            UpdateRect();
        }

        protected void UpdateRect()
        {
            m_Tracker.Clear();

            if(m_aspectMode == AspectMode.Disabled)
            {
                rectTransform.localScale = new Vector3(1f, 1f, rectTransform.localScale.z);
            }

            if (!IsActive()
                || m_aspectMode == AspectMode.Disabled)
            {
                return;
            }

            // add tracker
            m_Tracker.Add(this, rectTransform,
                          DrivenTransformProperties.ScaleX
                          | DrivenTransformProperties.ScaleY);

            // calc scales
            AspectMode calcMode = m_aspectMode;
            Vector2 parentSize = GetParentSize();
            Vector2 thisSize = rectTransform.rect.size;

            float xScale = 1f;
            if(thisSize.x != 0f)
            {
                xScale = parentSize.x / thisSize.x;
            }
            else
            {
                if(calcMode == AspectMode.WidthControlsHeight)
                {
                    calcMode = AspectMode.Disabled;
                }
                else if(calcMode == AspectMode.FitInParent
                        || calcMode == AspectMode.EnvelopeParent)
                {
                    calcMode = AspectMode.HeightControlsWidth;
                }
            }

            float yScale = 1f;
            if(thisSize.y != 0f)
            {
                yScale = parentSize.y / thisSize.y;
            }
            else
            {
                if(calcMode == AspectMode.HeightControlsWidth)
                {
                    calcMode = AspectMode.Disabled;
                }
                else if(calcMode == AspectMode.FitInParent
                        || calcMode == AspectMode.EnvelopeParent)
                {
                    calcMode = AspectMode.WidthControlsHeight;
                }
            }

            // add anchor control
            if(m_aspectMode == AspectMode.FitInParent
               || m_aspectMode == AspectMode.EnvelopeParent
               || m_aspectMode == AspectMode.StretchIgnoreAspect)
            {
                m_Tracker.Add(this, rectTransform,
                              DrivenTransformProperties.Anchors
                              | DrivenTransformProperties.AnchoredPosition
                              | DrivenTransformProperties.Pivot);

                rectTransform.pivot
                    = rectTransform.anchorMin
                    = rectTransform.anchorMax
                    = new Vector2(0.5f, 0.5f);

                rectTransform.anchoredPosition = Vector2.zero;
            }

            // apply scaling
            switch(calcMode)
            {
                case AspectMode.Disabled:
                {
                    xScale = yScale = 1f;
                    break;
                }
                case AspectMode.HeightControlsWidth:
                {
                    xScale = yScale;
                    break;
                }
                case AspectMode.WidthControlsHeight:
                {
                    yScale = xScale;
                    break;
                }
                case AspectMode.FitInParent:
                {
                    xScale = yScale = Mathf.Min(xScale, yScale);
                    break;
                }
                case AspectMode.EnvelopeParent:
                {
                    xScale = yScale = Mathf.Max(xScale, yScale);
                    break;
                }
                // case AspectMode.StretchIgnoreAspect
                // No modifications necessary
            }

            rectTransform.localScale = new Vector3(xScale, yScale, rectTransform.localScale.z);
        }

        private Vector2 GetParentSize()
        {
            RectTransform parent = rectTransform.parent as RectTransform;
            if (!parent)
                return Vector2.zero;
            return parent.rect.size;
        }

        /// <summary>
        /// Method called by the layout system. Has no effect
        /// </summary>
        public virtual void SetLayoutHorizontal() {}

        /// <summary>
        /// Method called by the layout system. Has no effect
        /// </summary>
        public virtual void SetLayoutVertical() {}

        /// <summary>
        /// Mark the AspectRatioFitter as dirty.
        /// </summary>
        protected void SetDirty()
        {
            UpdateRect();
        }

    #if UNITY_EDITOR
        protected override void OnValidate()
        {
            m_DelayedSetDirty = true;
        }
    #endif
    }
}
