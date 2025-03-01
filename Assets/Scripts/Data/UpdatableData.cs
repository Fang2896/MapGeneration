using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu]
public class UpdatableData : ScriptableObject
{
    public event System.Action OnValueUpdated;
    public bool autoUpdate;

    protected virtual void OnValidate() {
        if (autoUpdate) {
            NotifyOfUpdateValues();
        }
    }

    public void NotifyOfUpdateValues() {
        if (OnValueUpdated != null) {
            OnValueUpdated();
        }
    }
}
