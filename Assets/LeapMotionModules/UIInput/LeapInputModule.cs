﻿//The MIT License (MIT)

//Copyright (c) 2015 VREAL INC.
//Copyright (c) 2015 LEAP MOTION INC.

//Permission is hereby granted, free of charge, to any person obtaining a copy
//of this software and associated documentation files (the "Software"), to deal
//in the Software without restriction, including without limitation the rights
//to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
//copies of the Software, and to permit persons to whom the Software is
//furnished to do so, subject to the following conditions:

//The above copyright notice and this permission notice shall be included in all
//copies or substantial portions of the Software.

//THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
//IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
//AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
//LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
//OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
//SOFTWARE.


using UnityEngine;
using UnityEngine.UI;
using UnityEngine.VR;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.Serialization;
using System.Collections.Generic;
using System.Linq;
using Leap.Unity;
using Leap;

public class LeapInputModule : BaseInputModule
{

    [Header(" [Interaction Setup]")]
    [Tooltip("The current Leap Data Provider for the scene.")]
    public LeapProvider LeapDataProvider;
    [Tooltip("An optional alternate detector for pinching on the left hand.")]
    public Leap.Unity.PinchUtility.LeapPinchDetector LeftHandDetector;
    [Tooltip("An optional alternate detector for pinching on the right hand.")]
    public Leap.Unity.PinchUtility.LeapPinchDetector RightHandDetector;
    [Tooltip("How many hands and pointers the Input Module should allocate for.")]
    public int NumberOfHands = 2;
    [Tooltip("The distance from a UI element that interaction switches from Projective-Pointer based to Touch based.")]
    public float ProjectiveToTactileTransitionDistance = 0.12f;
    [Tooltip("When not using a PinchDetector, the distance in mm that the tip of the thumb and forefinger should be to activate selection during projective interaction.")]
    public float PinchingThreshold = 20f;
    [Tooltip("If the ScrollView still doesn't work even after disabling RaycastTarget on the intermediate layers.")]
    public bool OverrideScrollViewClicks = false;
    [Tooltip("Draw the raycast for projective interaction.")]
    public bool DrawDebug = false;

    //Customizable Pointer Parameters
    [Header(" [Pointer setup]")]
    [Tooltip("The sprite used to represent your pointers during projective interaction.")]
    public Sprite PointerSprite;
    [Tooltip("The material to be instantiated for your pointers during projective interaction.")]
    public Material PointerMaterial;
    [Tooltip("The size of the pointer in world coordinates.")]
    public float NormalPointerScale = 0.00025f; //In world space
    [Tooltip("The color of the pointer when it is hovering over blank canvas.")]
    [ColorUsageAttribute(true, false, 0, 8, 0.125f, 3)]
    public Color NormalColor = Color.white;
    [Tooltip("The color of the pointer when it is hovering over any other UI element.")]
    [ColorUsageAttribute(true, false, 0, 8, 0.125f, 3)]
    public Color HoveringColor = Color.green;
    [Tooltip("The sound that is played when the pointer transitions from canvas to element.")]
    public AudioClip HoverSound;
    [Tooltip("The color of the pointer when it is triggering a UI element.")]
    [ColorUsageAttribute(true, false, 0, 8, 0.125f, 3)]
    public Color TriggeringColor = Color.gray;
    [Tooltip("The sound that is played when the pointer triggers a UI element.")]
    public AudioClip TriggerSound;
    [Tooltip("The color of the pointer when it is triggering blank canvas.")]
    [ColorUsageAttribute(true, false, 0, 8, 0.125f, 3)]
    public Color TriggerMissedColor = Color.gray;
    [Tooltip("The sound that is played when the pointer triggers blank canvas.")]
    public AudioClip MissedSound;

    // Event delegates triggered by Input
    [System.Serializable]

    public class PositionEvent : UnityEvent<Vector3> {}

    [Tooltip("The event that is triggered upon clicking on a non-canvas UI element.")]
    public PositionEvent onClickDown;
    [Tooltip("The event that is triggered upon lifting up from a non-canvas UI element (Not 1:1 with onClickDown!)")]
    public PositionEvent onClickUp;
    [Tooltip("The event that is triggered upon hovering over a non-canvas UI element.")]
    public PositionEvent onHover;
    [Tooltip("The event that is triggered while holding down a non-canvas UI element.")]
    public PositionEvent whileClickHeld;

    //Event related data
    private Camera EventCamera;
    private PointerEventData[] PointEvents;
    private pointerStates[] pointerState;
    private RectTransform[] Pointers;
    private LineRenderer[] PointerLines;

    //Objects selected by pointer
    private GameObject[] CurrentPoint;
    private GameObject[] CurrentPressed;
    private GameObject[] CurrentDragging;

    //Values from the previous frame
    private pointerStates[] PrevState;
    private Vector2[] PrevScreenPosition;
    private bool[] PrevTriggeringInteraction;
    private bool PrevTouchingMode;

    //Misc. Objects
    private Canvas[] canvases;
    private Quaternion CurrentRotation;
    private AudioSource SoundPlayer;
    private Frame curFrame;

    //Queue of Spheres to Debug Draw
    private Queue<Vector3> DebugSphereQueue;

    enum pointerStates : int
    {
        OnCanvas,
        OnElement,
        PinchingToCanvas,
        PinchingToElement,
        NearCanvas,
        TouchingCanvas,
        TouchingElement,
        OffCanvas
    };

    //Initialization
    protected override void Start()
    {
        base.Start();

        if (LeapDataProvider == null)
        {
            LeapDataProvider = FindObjectOfType<LeapProvider>();
            if (LeapDataProvider == null || !LeapDataProvider.isActiveAndEnabled)
            {
                Debug.LogError("Cannot use LeapImageRetriever if there is no LeapProvider!");
                enabled = false;
                return;
            }
        }

        //Camera from which rays into the UI will be cast.
        EventCamera = new GameObject("UI Selection Camera").AddComponent<Camera>();
        EventCamera.clearFlags = CameraClearFlags.Nothing;
        EventCamera.enabled = false;
        EventCamera.nearClipPlane = 0.01f;
        EventCamera.fieldOfView = 179f;
        EventCamera.transform.SetParent(this.transform);

        //Setting the event camera of all currently existent Canvases to our Event Camera
        canvases = GameObject.FindObjectsOfType<Canvas>();
        foreach (Canvas canvas in canvases)
        {
            canvas.worldCamera = EventCamera;
        }

        //Initialize the Pointers for Projective Interaction
        Pointers = new RectTransform[NumberOfHands];
        PointerLines = new LineRenderer[NumberOfHands];
        for (int index = 0; index < Pointers.Length; index++)
        {
            //Create the Canvas to render the Pointer on
            GameObject pointer = new GameObject("Pointer " + index);
            Canvas canvas = pointer.AddComponent<Canvas>();
            pointer.AddComponent<CanvasRenderer>();
            pointer.AddComponent<CanvasScaler>();

            //Set the canvas to be worldspace like everything else
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 1000;

            //Add your sprite to the Canvas
            UnityEngine.UI.Image image = pointer.AddComponent<UnityEngine.UI.Image>();
            image.sprite = PointerSprite;
            image.material = Instantiate(PointerMaterial); //Make sure to instantiate the material so each pointer can be modified independently
            image.raycastTarget = false;

            PointerLines[index] = pointer.AddComponent<LineRenderer>();
            PointerLines[index].material = Instantiate(PointerMaterial);
            PointerLines[index].material.color = new Color(0f, 0f, 0f, 0f);
            PointerLines[index].SetVertexCount(2);
            PointerLines[index].SetWidth(0.001f, 0.001f);

            if (PointerSprite == null)
                Debug.LogError("Set PointerSprite on " + this.gameObject.name + " to the sprite you want to use as your pointer.", this.gameObject);

            Pointers[index] = pointer.GetComponent<RectTransform>();
        }

        //Initialize our Sound Player
        SoundPlayer = this.gameObject.AddComponent<AudioSource>();

        //Initialize the arrays that store persistent objects per pointer
        PointEvents = new PointerEventData[NumberOfHands];
        pointerState = new pointerStates[NumberOfHands];
        CurrentPoint = new GameObject[NumberOfHands];
        CurrentPressed = new GameObject[NumberOfHands];
        CurrentDragging = new GameObject[NumberOfHands];
        PrevTriggeringInteraction = new bool[NumberOfHands];
        PrevScreenPosition = new Vector2[NumberOfHands];
        PrevState = new pointerStates[NumberOfHands];

        //Used for calculating the origin of the Projective Interactions
        CurrentRotation = InputTracking.GetLocalRotation(VRNode.Head);

        //Initializes the Queue of Spheres to draw in OnDrawGizmos
        if (DrawDebug)
        {
            DebugSphereQueue = new Queue<Vector3>();
        }
    }

    //Update the Head Yaw for Calculating "Shoulder Positions"
    void Update()
    {
        Quaternion HeadYaw = Quaternion.Euler(0f, InputTracking.GetLocalRotation(VRNode.Head).eulerAngles.y, 0f);
        CurrentRotation = Quaternion.Slerp(CurrentRotation, HeadYaw, 0.1f);
    }

    //Process is called by UI system to process events
    public override void Process()
    {
        curFrame = LeapDataProvider.CurrentFrame.TransformedCopy(LeapTransform.Identity);

        //Send update events if there is a selected object
        //This is important for InputField to receive keyboard events
        SendUpdateEventToSelectedObject();

        //Begin Processing Each Hand
        for (int whichHand = 0; whichHand < NumberOfHands; whichHand++)
        {
            //Move on if this hand isn't visible in the frame
            if (curFrame.Hands.Count - 1 < whichHand)
            {
                if (Pointers[whichHand].gameObject.activeInHierarchy == true)
                {
                    Pointers[whichHand].gameObject.SetActive(false);
                }
                continue;
            }

            //Calculate Shoulder Positions (for Projection)
            Vector3 ProjectionOrigin = Vector3.zero;
            switch (curFrame.Hands[whichHand].IsRight)
            {
                case true:
                    ProjectionOrigin = InputTracking.GetLocalPosition(VRNode.Head) + CurrentRotation * new Vector3(0.15f, -0.2f, 0f);
                    break;
                case false:
                    ProjectionOrigin = InputTracking.GetLocalPosition(VRNode.Head) + CurrentRotation * new Vector3(-0.15f, -0.2f, 0f);
                    break;
            }

            //Draw Shoulders as Spheres, and the Raycast as a Line
            if (DrawDebug)
            {
                DebugSphereQueue.Enqueue(ProjectionOrigin);
                Debug.DrawRay(ProjectionOrigin, CurrentRotation * Vector3.forward * 5f);
            }

            //Raycast from shoulder through tip of the index finger to the UI
            bool TipRaycast = GetLookPointerEventData(whichHand, ProjectionOrigin, CurrentRotation * Vector3.forward, true);
            PrevState[whichHand] = pointerState[whichHand]; //Store old state for sound transitionary purposes
            UpdatePointer(whichHand, PointEvents[whichHand]);
            ProcessState(whichHand, TipRaycast);

            //If didn't hit anything near the fingertip, try doing it again, but through the knuckle this time
            if (pointerState[whichHand] == pointerStates.OffCanvas)
            {
                TipRaycast = GetLookPointerEventData(whichHand, ProjectionOrigin, CurrentRotation * Vector3.forward, false);
                UpdatePointer(whichHand, PointEvents[whichHand]);
                ProcessState(whichHand, TipRaycast);
            }

            PointerLines[whichHand].SetPosition(0, EventCamera.transform.position);
            PointerLines[whichHand].SetPosition(1, Pointers[whichHand].transform.position);

            //Trigger events that come from changing pointer state
            ProcessStateEvents(whichHand);

            //Tell Leap Buttons how far away the finger is
            if(PointEvents[whichHand].pointerCurrentRaycast.gameObject !=  null){
                ILeapWidget comp = PointEvents[whichHand].pointerCurrentRaycast.gameObject.GetComponent<ILeapWidget>();
                if (comp != null)
                {
                    ((ILeapWidget)comp).HoverDistance(distanceOfIndexTipToPointer(whichHand));
                }
            }

            //If we hit something with our Raycast, let's see if we should interact with it
            if (PointEvents[whichHand].pointerCurrentRaycast.gameObject != null && pointerState[whichHand] != pointerStates.OffCanvas)
            {
                CurrentPoint[whichHand] = PointEvents[whichHand].pointerCurrentRaycast.gameObject;

                //Trigger Enter or Exit Events on the UI Element (like highlighting)
                base.HandlePointerExitAndEnter(PointEvents[whichHand], CurrentPoint[whichHand]);

                //If we weren't triggering an interaction last frame, but we are now...
                if (!PrevTriggeringInteraction[whichHand] && isTriggeringInteraction(whichHand))
                {
                    PrevTriggeringInteraction[whichHand] = true;

                    //Deselect all objects
                    if (base.eventSystem.currentSelectedGameObject)
                    {
                        base.eventSystem.SetSelectedGameObject(null);
                    }

                    //Record pointer telemetry
                    PointEvents[whichHand].pressPosition = PointEvents[whichHand].position;
                    PointEvents[whichHand].pointerPressRaycast = PointEvents[whichHand].pointerCurrentRaycast;
                    PointEvents[whichHand].pointerPress = null; //Clear this for setting later

                    //If we hit something good, let's trigger it!
                    if (CurrentPoint[whichHand] != null)
                    {
                        CurrentPressed[whichHand] = CurrentPoint[whichHand];

                        //See if this object, or one of its parents, has a pointerDownHandler
                        GameObject newPressed = ExecuteEvents.ExecuteHierarchy(CurrentPressed[whichHand], PointEvents[whichHand], ExecuteEvents.pointerDownHandler);

                        //If not, see if one has a pointerClickHandler!
                        if (newPressed == null)
                        {
                            newPressed = ExecuteEvents.ExecuteHierarchy(CurrentPressed[whichHand], PointEvents[whichHand], ExecuteEvents.pointerClickHandler);
                            if (newPressed != null)
                            {
                                CurrentPressed[whichHand] = newPressed;
                            }
                        }
                        else
                        {
                            CurrentPressed[whichHand] = newPressed;
                            //We want to do "click on button down" at same time, unlike regular mouse processing
                            //Which does click when mouse goes up over same object it went down on
                            //This improves the user's ability to select small menu items
                            ExecuteEvents.Execute(newPressed, PointEvents[whichHand], ExecuteEvents.pointerClickHandler);

                        }

                        if (newPressed != null)
                        {
                            PointEvents[whichHand].pointerPress = newPressed;
                            CurrentPressed[whichHand] = newPressed;

                            //Select the currently pressed object
                            if (ExecuteEvents.GetEventHandler<ISelectHandler>(CurrentPressed[whichHand]))
                            {
                                base.eventSystem.SetSelectedGameObject(CurrentPressed[whichHand]);
                            }
                        }

                        ExecuteEvents.Execute(CurrentPressed[whichHand], PointEvents[whichHand], ExecuteEvents.beginDragHandler);
                        PointEvents[whichHand].pointerDrag = CurrentPressed[whichHand];
                        CurrentDragging[whichHand] = CurrentPressed[whichHand];
                    }
                }

                //If we WERE interacting last frame, but are not this frame...
                if (PrevTriggeringInteraction[whichHand] && !isTriggeringInteraction(whichHand))
                {
                    PrevTriggeringInteraction[whichHand] = false;

                    if (CurrentDragging[whichHand])
                    {
                        ExecuteEvents.Execute(CurrentDragging[whichHand], PointEvents[whichHand], ExecuteEvents.endDragHandler);
                        if (CurrentPoint[whichHand] != null)
                        {
                            ExecuteEvents.ExecuteHierarchy(CurrentPoint[whichHand], PointEvents[whichHand], ExecuteEvents.dropHandler);
                        }
                        PointEvents[whichHand].pointerDrag = null;
                        PointEvents[whichHand].dragging = false;
                        CurrentDragging[whichHand] = null;
                    }
                    if (CurrentPressed[whichHand])
                    {
                        ExecuteEvents.Execute(CurrentPressed[whichHand], PointEvents[whichHand], ExecuteEvents.pointerUpHandler);
                        PointEvents[whichHand].rawPointerPress = null;
                        PointEvents[whichHand].pointerPress = null;
                        CurrentPressed[whichHand] = null;
                    }
                }

                //And for everything else, there is dragging.
                if (CurrentDragging[whichHand] != null)
                {
                    ExecuteEvents.Execute(CurrentDragging[whichHand], PointEvents[whichHand], ExecuteEvents.dragHandler);
                }
            }

            updatePointerColor(whichHand);
        }

        //Make the special Leap Widget Buttons Pop Up and Flatten when Appropriate
        if (PrevTouchingMode != getTouchingMode())
        {
            PrevTouchingMode = getTouchingMode();
            if (PrevTouchingMode)
            {
                foreach (Canvas canvas in canvases)
                {
                    canvas.BroadcastMessage("Expand", SendMessageOptions.DontRequireReceiver);
                }
            }
            else
            {
                foreach (Canvas canvas in canvases)
                {
                    canvas.BroadcastMessage("Retract", SendMessageOptions.DontRequireReceiver);
                }
            }
        }
    }

    //Raycast from the EventCamera into UI Space
    private bool GetLookPointerEventData(int whichHand, Vector3 Origin, Vector3 Direction, bool forceTipRaycast)
    {
        //Whether or not this will be a raycast through the finger tip
        bool TipRaycast = false;

        //Initialize a blank PointerEvent
        if (PointEvents[whichHand] == null)
        {
            PointEvents[whichHand] = new PointerEventData(base.eventSystem);
        }
        else
        {
            PointEvents[whichHand].Reset();
        }

        //We're always going to assume we're "Left Clicking", for the benefit of uGUI
        PointEvents[whichHand].button = PointerEventData.InputButton.Left;

        //If we're in "Touching Mode", Raycast through the finger
        Vector3 IndexFingerPosition;
        if (pointerState[whichHand] == pointerStates.NearCanvas || pointerState[whichHand] == pointerStates.TouchingCanvas || pointerState[whichHand] == pointerStates.TouchingElement || forceTipRaycast)
        {
            TipRaycast = true;
            EventCamera.transform.position = InputTracking.GetLocalPosition(VRNode.Head);
            IndexFingerPosition = curFrame.Hands[whichHand].Fingers[1].Bone(Bone.BoneType.TYPE_DISTAL).Center.ToVector3();//StabilizedTipPosition.ToVector3();
           // IndexFingerPosition = curFrame.Hands[whichHand].Fingers[1].StabilizedTipPosition.ToVector3();
        }
        else //Raycast through knuckle of Index Finger
        {
            EventCamera.transform.position = Origin;
            //IndexFingerPosition = Vector3.Lerp(LeapDataProvider.CurrentFrame.Hands[whichHand].Fingers[1].TipPosition.ToVector3(), LeapDataProvider.CurrentFrame.Hands[whichHand].Fingers[0].TipPosition.ToVector3(), 0.7f);
            IndexFingerPosition = curFrame.Hands[whichHand].Fingers[1].Bone(Bone.BoneType.TYPE_METACARPAL).Center.ToVector3();
        }

        //Draw Camera Origin
        if (DrawDebug)
            DebugSphereQueue.Enqueue(EventCamera.transform.position);

        //Set EventCamera's Forward Direction
        EventCamera.transform.forward = Direction;

        //Set the Raycast Direction and Delta
        PointEvents[whichHand].position = Vector2.Lerp(PrevScreenPosition[whichHand], EventCamera.WorldToScreenPoint(IndexFingerPosition), 1.0f);//new Vector2(Screen.width / 2, Screen.height / 2);
        PointEvents[whichHand].delta = (PointEvents[whichHand].position - PrevScreenPosition[whichHand]) * -10f;
        PrevScreenPosition[whichHand] = PointEvents[whichHand].position;
        PointEvents[whichHand].scrollDelta = Vector2.zero;

        //Perform the Raycast and sort all the things we hit by distance...
        base.eventSystem.RaycastAll(PointEvents[whichHand], m_RaycastResultCache);
        m_RaycastResultCache = m_RaycastResultCache.OrderBy(o => o.distance).ToList();

        //If the Canvas and an Element are Z-Fighting, remove the Canvas from the runnings
        if (m_RaycastResultCache.Count > 1)
        {
            if (m_RaycastResultCache[0].gameObject.GetComponent<Canvas>())
            {
                m_RaycastResultCache.RemoveAt(0);
            }
        }

        //Optional hack that subverts ScrollRect hierarchies; to avoid this, disable "RaycastTarget" on the Viewport and Content panes
        if (OverrideScrollViewClicks)
        {
            PointEvents[whichHand].pointerCurrentRaycast = new RaycastResult();
            foreach (RaycastResult hit in m_RaycastResultCache)
            {
                if (hit.gameObject.GetComponent<Scrollbar>() != null)
                {
                    PointEvents[whichHand].pointerCurrentRaycast = hit;
                }
                else if (PointEvents[whichHand].pointerCurrentRaycast.gameObject == null && hit.gameObject.GetComponent<ScrollRect>() != null)
                {
                    PointEvents[whichHand].pointerCurrentRaycast = hit;
                }
            }
            if (PointEvents[whichHand].pointerCurrentRaycast.gameObject == null)
            {
                PointEvents[whichHand].pointerCurrentRaycast = FindFirstRaycast(m_RaycastResultCache);
            }
        }
        else
        {
            PointEvents[whichHand].pointerCurrentRaycast = FindFirstRaycast(m_RaycastResultCache);
        }

        //Clear the list of things we hit; we don't need it anymore.
        m_RaycastResultCache.Clear();

        return TipRaycast;
    }

    //Tree to decide the State of the Pointer
    private void ProcessState(int whichHand, bool forceTipRaycast)
    {
        if ((PointEvents[whichHand].pointerCurrentRaycast.gameObject != null))
        {
            if (distanceOfIndexTipToPointer(whichHand) < ProjectiveToTactileTransitionDistance)
            {
                if (isTriggeringInteraction(whichHand))
                {
                    if (!PointEvents[whichHand].pointerCurrentRaycast.gameObject.GetComponent<Canvas>())
                    {
                        pointerState[whichHand] = pointerStates.TouchingElement;
                    }
                    else
                    {
                        pointerState[whichHand] = pointerStates.TouchingCanvas;
                    }
                }
                else
                {
                    pointerState[whichHand] = pointerStates.NearCanvas;
                }
            }
            else if (!forceTipRaycast)
            {
                if (!PointEvents[whichHand].pointerCurrentRaycast.gameObject.GetComponent<Canvas>())
                {
                    if (isTriggeringInteraction(whichHand))
                    {
                        pointerState[whichHand] = pointerStates.PinchingToElement;
                    }
                    else
                    {
                        pointerState[whichHand] = pointerStates.OnElement;
                    }
                }
                else
                {
                    if (isTriggeringInteraction(whichHand))
                    {
                        pointerState[whichHand] = pointerStates.PinchingToCanvas;
                    }
                    else
                    {
                        pointerState[whichHand] = pointerStates.OnCanvas;
                    }
                }
            }
            else
            {
                pointerState[whichHand] = pointerStates.OffCanvas;
            }
        }
        else
        {
            pointerState[whichHand] = pointerStates.OffCanvas;
        }
    }

    //Discrete 1-Frame Transition Behaviors like Sounds and Events
    //(color changing is in a different function since it is lerped over multiple frames)
    private void ProcessStateEvents(int whichHand)
    {
        if (PrevState[whichHand] == pointerStates.OnCanvas)
        {
            if (pointerState[whichHand] == pointerStates.OnElement)
            {
                //When you begin to hover on an element
                SoundPlayer.PlayOneShot(HoverSound);
                onHover.Invoke(Pointers[whichHand].transform.position);
            }
            else if (pointerState[whichHand] == pointerStates.PinchingToCanvas)
            {
                //When you try to interact with the Canvas
                SoundPlayer.PlayOneShot(MissedSound);
            }
        }
        else if (PrevState[whichHand] == pointerStates.OnElement)
        {
            if (pointerState[whichHand] == pointerStates.OnCanvas)
            {
                //When you begin to hover over the Canvas after hovering over an element
                SoundPlayer.PlayOneShot(HoverSound);
            }
            else if (pointerState[whichHand] == pointerStates.PinchingToElement)
            {
                //When you click on an element
                SoundPlayer.PlayOneShot(TriggerSound);
                onClickDown.Invoke(Pointers[whichHand].transform.position);
            }//ALSO PLAY HOVER SOUND IF ON DIFFERENT UI ELEMENT THAN LAST FRAME
        }
        else if (PrevState[whichHand] == pointerStates.PinchingToElement)
        {
            if (pointerState[whichHand] == pointerStates.PinchingToCanvas)
            {
                //When you slide off of an element while holding it
                //SoundPlayer.PlayOneShot(HoverSound);
            }
            else if (pointerState[whichHand] == pointerStates.OnElement || pointerState[whichHand] == pointerStates.OnCanvas)
            {
                //When you let go of an element
                onClickUp.Invoke(Pointers[whichHand].transform.position);
            }
        }
        else if (PrevState[whichHand] == pointerStates.NearCanvas)
        {
            if (pointerState[whichHand] == pointerStates.TouchingElement)
            {
                //When you physically touch an element
                SoundPlayer.PlayOneShot(TriggerSound);
                onClickDown.Invoke(Pointers[whichHand].transform.position);
            }
            if (pointerState[whichHand] == pointerStates.TouchingCanvas)
            {
                //When you physically touch Blank Canvas
                SoundPlayer.PlayOneShot(MissedSound);
            }
        }
        else if (PrevState[whichHand] == pointerStates.TouchingElement)
        {
            if (pointerState[whichHand] == pointerStates.NearCanvas)
            {
                //When you physically pull out of an element
                onClickUp.Invoke(Pointers[whichHand].transform.position);
            }
        }
    }

    //Update the cursor location and whether or not it is enabled
    private void UpdatePointer(int index, PointerEventData pointData)
    {
        if (PointEvents[index].pointerCurrentRaycast.gameObject != null)
        {
            Pointers[index].gameObject.SetActive(true);
            if (PointEvents[index].pointerCurrentRaycast.gameObject != null)
            {
                RectTransform draggingPlane = PointEvents[index].pointerCurrentRaycast.gameObject.GetComponent<RectTransform>();
                Vector3 globalLookPos;
                if (RectTransformUtility.ScreenPointToWorldPointInRectangle(draggingPlane, pointData.position, pointData.enterEventCamera, out globalLookPos))
                {
                    Pointers[index].position = globalLookPos;// -transform.forward * 0.01f; //Amount the pointer floats above the Canvas

                    float pointerAngle = Mathf.Rad2Deg * (Mathf.Atan2(pointData.delta.x, pointData.delta.y));
                    Pointers[index].rotation = draggingPlane.rotation * Quaternion.Euler(0f, 0f, -pointerAngle);

                    // scale cursor based on distance to camera
                    float lookPointDistance = 1f;
                    if (Camera.main != null)
                    {
                        lookPointDistance = (Pointers[index].position - Camera.main.transform.position).magnitude;
                    }
                    else
                    {
                        Debug.LogError("Tag a camera with 'Main Camera'");
                    }

                    float Pointerscale = lookPointDistance * NormalPointerScale;
                    if (Pointerscale < NormalPointerScale)
                    {
                        Pointerscale = NormalPointerScale;
                    }

                    //Commented out Velocity Stretching because it looks funny when I change the projection origin
                    Pointers[index].localScale = Pointerscale * new Vector3(1f, 1f/* + pointData.delta.magnitude*0.5f*/, 1f);
                }
            }
        }
        else
        {
            Pointers[index].gameObject.SetActive(false);
        }
    }

    //A boolean that returns when a "click" is being triggered
    public bool isTriggeringInteraction(int whichHand)
    {

        if (curFrame.Hands[whichHand].IsRight && RightHandDetector != null && RightHandDetector.IsPinching)
        {
            return true;
        }
        else if (!curFrame.Hands[whichHand].IsRight && LeftHandDetector != null && LeftHandDetector.IsPinching)
        {
            return true;
        }

        if ((pointerState[whichHand] == pointerStates.NearCanvas || pointerState[whichHand] == pointerStates.TouchingCanvas || pointerState[whichHand] == pointerStates.TouchingElement))
        {
            return (distanceOfIndexTipToPointer(whichHand) < 0f);
        }


        return curFrame.Hands[whichHand].PinchDistance < PinchingThreshold;

        //return false;
    }

    //The z position of the index finger tip to the Pointer
    public float distanceOfIndexTipToPointer(int whichHand)
    {
        //Get Base of Index Finger Position
        Vector3 IndexTipPosition = curFrame.Hands[whichHand].Fingers[1].Bone(Bone.BoneType.TYPE_DISTAL).NextJoint.ToVector3();
        //Debug.Log(-Pointers[whichHand].InverseTransformPoint(IndexTipPosition).z * Pointers[whichHand].localScale.z);
        return -Pointers[whichHand].InverseTransformPoint(IndexTipPosition).z * Pointers[whichHand].localScale.z;
    }

    public bool getTouchingMode()
    {
        bool mode = false;
        foreach (pointerStates state in pointerState)
        {
            if (state == pointerStates.NearCanvas || state == pointerStates.TouchingCanvas || state == pointerStates.TouchingElement)
            {
                mode = true;
            }
        }
        return mode;
    }

    //Where the color that the Pointer will lerp to is chosen
    void updatePointerColor(int whichHand)
    {
        switch (pointerState[whichHand])
        {
            case pointerStates.OnCanvas:
                lerpPointerColor(whichHand, new Color(0f, 0f, 0f, 1f), 0.2f);
                lerpPointerColor(whichHand, NormalColor, 0.2f);
                break;
            case pointerStates.OnElement:
                lerpPointerColor(whichHand, new Color(0f, 0f, 0f, 1f), 0.2f);
                lerpPointerColor(whichHand, HoveringColor, 0.2f);
                break;
            case pointerStates.PinchingToCanvas:
                lerpPointerColor(whichHand, new Color(0f, 0f, 0f, 1f), 0.2f);
                lerpPointerColor(whichHand, TriggerMissedColor, 0.2f);
                break;
            case pointerStates.PinchingToElement:
                lerpPointerColor(whichHand, new Color(0f, 0f, 0f, 1f), 0.2f);
                lerpPointerColor(whichHand, TriggeringColor, 0.2f);
                break;
            case pointerStates.NearCanvas:
                lerpPointerColor(whichHand, new Color(0.0f, 0.0f, 0.0f, 0f), 1f);
                break;
            case pointerStates.TouchingElement:
                lerpPointerColor(whichHand, new Color(0.0f, 0.0f, 0.0f, 0f), 0.2f);
                break;
            case pointerStates.TouchingCanvas:
                lerpPointerColor(whichHand, new Color(0.0f, 0.01f, 0.0f, 0f), 0.2f);
                break;
            case pointerStates.OffCanvas:
                lerpPointerColor(whichHand, new Color(0.0f, 0.0f, 0.0f, 0f), 0.2f);
                break;
        }
    }

    //Where the lerping of the cursor's color takes place
    //If RGB are 0f or Alpha is 1f, then it will ignore those components and only lerp the remaining components
    public void lerpPointerColor(int whichHand, Color color, float lerpalpha)
    {
        Color oldColor = Pointers[whichHand].GetComponent<UnityEngine.UI.Image>().material.color;
        if (color.r == 0f && color.g == 0f && color.b == 0f)
        {
            Pointers[whichHand].GetComponent<UnityEngine.UI.Image>().material.color = Color.Lerp(oldColor, new Color(oldColor.r, oldColor.g, oldColor.b, color.a), lerpalpha);
        }
        else if (color.a == 1f)
        {
            Pointers[whichHand].GetComponent<UnityEngine.UI.Image>().material.color = Color.Lerp(oldColor, new Color(color.r, color.g, color.b, oldColor.a), lerpalpha);
        }
        else
        {
            Pointers[whichHand].GetComponent<UnityEngine.UI.Image>().material.color = Color.Lerp(oldColor, color, lerpalpha);
        }
    }

    private bool SendUpdateEventToSelectedObject()
    {
        if (base.eventSystem.currentSelectedGameObject == null)
            return false;

        BaseEventData data = GetBaseEventData();
        ExecuteEvents.Execute(base.eventSystem.currentSelectedGameObject, data, ExecuteEvents.updateSelectedHandler);
        return data.used;
    }

    void OnDrawGizmos()
    {
        if (DrawDebug)
        {
            while (DebugSphereQueue != null && DebugSphereQueue.Count > 0)
            {
                Gizmos.DrawSphere(DebugSphereQueue.Dequeue(), 0.1f);
            }
        }
    }
}