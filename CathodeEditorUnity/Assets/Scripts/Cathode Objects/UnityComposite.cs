using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public class UnityComposite : MonoBehaviour
{
    public Composite Composite => _composite;
    private Composite _composite = null;

    public bool Created => _created;
    private bool _created = false;

    private Dictionary<Entity, GameObject> _entityGOs = new Dictionary<Entity, GameObject>();

    public void CreateComposite(Composite composite)
    {
        for (int i = 0; i < transform.childCount; i++)
            Destroy(transform.GetChild(i));

        Debug.Log("Creating composite: " + composite.name);

        List<Entity> entities = composite.GetEntities();
        foreach (Entity entity in entities)
        {
            GameObject entityGO = null;
            
            //If this is a composite instance, we use the prefab.
            if (entity.variant == EntityVariant.FUNCTION && !CommandsUtils.FunctionTypeExists(((FunctionEntity)entity).function))
            {
                GameObject compositePrefab = UnityLevelContent.instance.GetCompositePrefab(((FunctionEntity)entity).function.ToUInt32());
                if (compositePrefab == null)
                    continue;
                entityGO = (GameObject)PrefabUtility.InstantiatePrefab(compositePrefab);
            }
            //Otherwise, create a new GameObject
            else
            {
                entityGO = new GameObject(entity.shortGUID.ToUInt32().ToString());
                switch (entity.variant)
                {
                    case EntityVariant.FUNCTION:
                        FunctionEntity function = (FunctionEntity)entity;
                        switch ((FunctionType)function.function.ToUInt32())
                        {
                            case FunctionType.ModelReference:
                                break;
                        }
                        break;
                }
            }

            entityGO.transform.SetParent(this.transform);
            UnityLevelContent.instance.SetLocalEntityTransform(entity, entityGO.transform);
            _entityGOs.Add(entity, entityGO);
        }

        _composite = composite;
        _created = true;
    }
}
