﻿using UnityEngine;
using LeapInternal;
using InteractionEngine.Internal;
using System;

namespace InteractionEngine {

  public class TestInteractionObject : InteractionObject {

    private SphereCollider _sphereCollider;
    private Renderer _renderer;

    public override LEAP_IE_TRANSFORM IeTransform {
      get {
        LEAP_IE_TRANSFORM ieTransform = new LEAP_IE_TRANSFORM();
        ieTransform.position = new LEAP_VECTOR(transform.position);
        ieTransform.rotation = new LEAP_QUATERNION(transform.rotation);
        return ieTransform;
      }
      set {
        transform.position = value.position.ToUnityVector();
        transform.rotation = value.rotation.ToUnityRotation();
      }
    }

    private StructMarshal<LEAP_IE_SPHERE_DESCRIPTION> _sphereMarshal = new StructMarshal<LEAP_IE_SPHERE_DESCRIPTION>();
    public override IntPtr ShapeDescription {
      get {
        LEAP_IE_SPHERE_DESCRIPTION sphereDesc = new LEAP_IE_SPHERE_DESCRIPTION();
        sphereDesc.shape.type = eLeapIEShapeType.eLeapIERS_ShapeSphere;
        sphereDesc.radius = _sphereCollider.radius;

        _sphereMarshal.ReleaseAllTemp();
        IntPtr ptr = _sphereMarshal.AllocNewTemp(sphereDesc);
        return ptr;
      }
    }

    public override void SetClassification(eLeapIEClassification classification) {
      switch (classification) {
        case eLeapIEClassification.eLeapIEClassification_None:
          _renderer.material.color = Color.white;
          break;
        case eLeapIEClassification.eLeapIEClassification_Grab:
          _renderer.material.color = Color.green;
          break;
        case eLeapIEClassification.eLeapIEClassification_Push:
          _renderer.material.color = Color.blue;
          break;
      }
    }

    protected override void Awake() {
      base.Awake();

      _sphereCollider = GetComponent<SphereCollider>();
      _renderer = GetComponent<Renderer>();
    }

  }
}
