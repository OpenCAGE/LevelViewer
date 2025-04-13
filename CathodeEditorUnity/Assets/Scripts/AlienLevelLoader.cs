using System.Collections.Generic;
using UnityEngine;
using CATHODE;
using System.IO;
using CathodeLib;
using CATHODE.LEGACY;
using static CATHODE.LEGACY.ShadersPAK;
using System.Threading.Tasks;
using CATHODE.Scripting;
using System.Linq;
using CATHODE.Scripting.Internal;
using System;
using UnityEditor;
using System.Collections;

public class AlienLevelLoader : MonoBehaviour
{
    public Action OnLoaded;

    private string _levelName = "";
    public string LevelName => _levelName;

    private GameObject _parentGameObject = null;
    public GameObject ParentGameObject => _parentGameObject;

    private Composite _loadedComposite = null;
    public uint CompositeID => _loadedComposite == null ? 0 : _loadedComposite.shortGUID.ToUInt32();
    public string CompositeIDString => _loadedComposite == null || _loadedComposite.shortGUID == ShortGuid.Invalid ? "" : _loadedComposite.shortGUID.ToByteString();
    public string CompositeName => _loadedComposite == null ? "" : _loadedComposite.name;

    private CommandsEditorConnection _client;

    IEnumerator Start()
    {
        _client = GetComponent<CommandsEditorConnection>();

        yield return new WaitForEndOfFrame();

        try
        {
            SceneView.FocusWindowIfItsOpen(typeof(SceneView));
            EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView")).Close();
        }
        catch { }
    }

    public void LoadComposite(ShortGuid guid)
    {
        if (_parentGameObject != null)
            Destroy(_parentGameObject);
        _parentGameObject = new GameObject(_levelName);
        _parentGameObject.hideFlags |= HideFlags.HideInHierarchy;
        _parentGameObject.hideFlags |= HideFlags.NotEditable;

    }

    /* Load Commands data */
    /*
    private void LoadComposite(Composite composite)
    {
        _loadedComposite = composite;
        ParseComposite(composite, _parentGameObject, null, Vector3.zero, Quaternion.identity, new List<AliasEntity>());
    }
    void ParseComposite(Composite composite, GameObject parentGO, Entity parentEntity, Vector3 parentPos, Quaternion parentRot, List<AliasEntity> aliases)
    {
        if (composite == null) return;
        GameObject compositeGO = parentEntity == null ? _parentGameObject : new GameObject(parentEntity.shortGUID.ToUInt32().ToString());
        compositeGO.transform.parent = parentGO.transform;
        compositeGO.transform.SetLocalPositionAndRotation(parentPos, parentRot);
        compositeGO.hideFlags |= HideFlags.NotEditable;

        //Compile all appropriate overrides, and keep the hierarchies trimmed so that index zero is accurate to this composite
        List<AliasEntity> trimmedAliases = new List<AliasEntity>();
        for (int i = 0; i < aliases.Count; i++)
        {
            List<ShortGuid> path = aliases[i].alias.path.ToList();
            path.RemoveAt(0);
            if (path.Count != 0)
                trimmedAliases.Add(aliases[i]);
        }
        trimmedAliases.AddRange(composite.aliases);
        aliases = trimmedAliases;

        //Parse all functions in this composite & handle them appropriately
        foreach (FunctionEntity function in composite.functions)
        {
            //Jump through to the next composite
            if (!CommandsUtils.FunctionTypeExists(function.function))
            {
                Composite compositeNext = _levelContent.CommandsPAK.GetComposite(function.function);
                if (compositeNext != null)
                {
                    //Find all overrides that are appropriate to take through to the next composite
                    List<AliasEntity> overridesNext = trimmedAliases.FindAll(o => o.alias.path[0] == function.shortGUID);

                    //Work out our position, accounting for overrides
                    Vector3 position, rotation;
                    AliasEntity ovrride = trimmedAliases.FirstOrDefault(o => o.alias.path.Length == (o.alias.path[o.alias.path.Length - 1] == ShortGuid.Invalid ? 2 : 1) && o.alias.path[0] == function.shortGUID);
                    if (!GetEntityTransform(ovrride, out position, out rotation))
                        GetEntityTransform(function, out position, out rotation);

                    //Continue
                    ParseComposite(compositeNext, compositeGO, function, position, Quaternion.Euler(rotation), overridesNext);
                }
            }

            //Parse model data
            else if (CommandsUtils.GetFunctionType(function.function) == FunctionType.ModelReference)
            {
                //Work out our position, accounting for overrides
                Vector3 position, rotation;
                AliasEntity ovrride = trimmedAliases.FirstOrDefault(o => o.alias.path.Length == 1 && o.alias.path[0] == function.shortGUID);
                if (!GetEntityTransform(ovrride, out position, out rotation))
                    GetEntityTransform(function, out position, out rotation);

                GameObject nodeModel = new GameObject(function.shortGUID.ToUInt32().ToString());
                nodeModel.transform.parent = compositeGO.transform;
                nodeModel.transform.SetLocalPositionAndRotation(position, Quaternion.Euler(rotation));
                nodeModel.hideFlags |= HideFlags.NotEditable;

                Parameter resourceParam = function.GetParameter("resource");
                if (resourceParam != null && resourceParam.content != null)
                {
                    switch (resourceParam.content.dataType)
                    {
                        case DataType.RESOURCE:
                            cResource resource = (cResource)resourceParam.content;
                            foreach (ResourceReference resourceRef in resource.value)
                            {
                                for (int i = 0; i < resourceRef.count; i++)
                                {
                                    RenderableElements.Element renderable = _levelContent.RenderableREDS.Entries[resourceRef.index + i];
                                    switch (resourceRef.resource_type)
                                    {
                                        case ResourceType.RENDERABLE_INSTANCE:
                                            SpawnRenderable(nodeModel, renderable.ModelIndex, renderable.MaterialIndex);
                                            break;
                                        case ResourceType.COLLISION_MAPPING:
                                            break;
                                    }
                                }
                            }
                            break;
                    }
                }
            }
        }
    }
    */

    #region Asset Handlers
    
    #endregion
}