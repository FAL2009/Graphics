using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor.Experimental.UIElements.GraphView;
using UnityEngine;
using UnityEngine.Graphing;
using UnityEngine.MaterialGraph;
using Object = UnityEngine.Object;

namespace UnityEditor.MaterialGraph.Drawing
{
    public interface IMaterialGraphEditWindow
    {
        void PingAsset();

        void UpdateAsset();

        void Repaint();

        void ToggleRequiresTime();
        void ToSubGraph();
    }

    public class MaterialGraphEditWindow : AbstractMaterialGraphEditWindow<UnityEngine.MaterialGraph.MaterialGraph>
    {}
    public class SubGraphEditWindow : AbstractMaterialGraphEditWindow<SubGraph>
    {}

    public abstract class AbstractMaterialGraphEditWindow<TGraphType> : EditorWindow, IMaterialGraphEditWindow where TGraphType : AbstractMaterialGraph
    {
        public static bool allowAlwaysRepaint = true;

        [SerializeField]
        Object m_Selected;

        [SerializeField]
        TGraphType m_InMemoryAsset;

        GraphEditorView m_GraphEditorView;

        public TGraphType inMemoryAsset
        {
            get { return m_InMemoryAsset; }
            set { m_InMemoryAsset = value; }
        }

        public Object selected
        {
            get { return m_Selected; }
            set { m_Selected = value; }
        }

        public MaterialGraphPresenter CreateDataSource()
        {
            return CreateInstance<MaterialGraphPresenter>();
        }

        public GraphView CreateGraphView()
        {
            return new MaterialGraphView(this);
        }

        void Update()
        {
            if (m_GraphEditorView != null)
            {
                m_GraphEditorView.presenter.UpdateTimeDependentNodes();
            }
        }

        void OnEnable()
        {
            m_GraphEditorView = new GraphEditorView(CreateGraphView());
            rootVisualContainer.Add(m_GraphEditorView);
            var source = CreateDataSource();
            source.Initialize(inMemoryAsset, this);
            m_GraphEditorView.presenter = source;
        }

        void OnDisable()
        {
            rootVisualContainer.Clear();
        }

        void OnGUI()
        {
            var presenter = m_GraphEditorView.presenter;
            var e = Event.current;

            if (e.type == EventType.ValidateCommand && (
                    e.commandName == "Copy" && presenter.canCopy
                    || e.commandName == "Paste" && presenter.canPaste
                    || e.commandName == "Duplicate" && presenter.canDuplicate
                    || e.commandName == "Cut" && presenter.canCut
                    || (e.commandName == "Delete" || e.commandName == "SoftDelete") && presenter.canDelete))
            {
                e.Use();
            }

            if (e.type == EventType.ExecuteCommand)
            {
                if (e.commandName == "Copy")
                    presenter.Copy();
                if (e.commandName == "Paste")
                    presenter.Paste();
                if (e.commandName == "Duplicate")
                    presenter.Duplicate();
                if (e.commandName == "Cut")
                    presenter.Cut();
                if (e.commandName == "Delete" || e.commandName == "SoftDelete")
                    presenter.Delete();
            }
        }

        public void PingAsset()
        {
            if (selected != null)
                EditorGUIUtility.PingObject(selected);
        }

        public void UpdateAsset()
        {
            if (selected != null && inMemoryAsset != null)
            {
                var path = AssetDatabase.GetAssetPath(selected);
                if (string.IsNullOrEmpty(path) || inMemoryAsset == null)
                {
                    return;
                }

                if (typeof(TGraphType) == typeof(UnityEngine.MaterialGraph.MaterialGraph))
                    UpdateShaderGraphOnDisk(path);

                if (typeof(TGraphType) == typeof(SubGraph))
                    UpdateShaderSubGraphOnDisk(path);
            }
        }

        public void ToSubGraph()
        {
            string path = EditorUtility.SaveFilePanelInProject("Save subgraph", "New SubGraph", "ShaderSubGraph", "");
            path = path.Replace(Application.dataPath, "Assets");
            if (path.Length == 0)
                return;

            var graphDataSource = m_GraphEditorView.presenter;
            var selected = graphDataSource.elements.Where(e => e.selected).ToArray();
            var deserialized = MaterialGraphPresenter.DeserializeCopyBuffer(JsonUtility.ToJson(MaterialGraphPresenter.CreateCopyPasteGraph(selected)));

            if (deserialized == null)
                return;

            var graph = new SubGraph();
            graph.AddNode(new SubGraphInputNode());
            graph.AddNode(new SubGraphOutputNode());

            var nodeGuidMap = new Dictionary<Guid, Guid>();
            foreach (var node in deserialized.GetNodes<INode>())
            {
                var oldGuid = node.guid;
                var newGuid = node.RewriteGuid();
                nodeGuidMap[oldGuid] = newGuid;
                graph.AddNode(node);
            }

            // remap outputs to the subgraph
            var inputEdgeNeedsRemap = new List<IEdge>();
            var outputEdgeNeedsRemap = new List<IEdge>();
            foreach (var edge in deserialized.edges)
            {
                var outputSlot = edge.outputSlot;
                var inputSlot = edge.inputSlot;

                Guid remappedOutputNodeGuid;
                Guid remappedInputNodeGuid;
                var outputRemapExists = nodeGuidMap.TryGetValue(outputSlot.nodeGuid, out remappedOutputNodeGuid);
                var inputRemapExists = nodeGuidMap.TryGetValue(inputSlot.nodeGuid, out remappedInputNodeGuid);

                // pasting nice internal links!
                if (outputRemapExists && inputRemapExists)
                {
                    var outputSlotRef = new SlotReference(remappedOutputNodeGuid, outputSlot.slotId);
                    var inputSlotRef = new SlotReference(remappedInputNodeGuid, inputSlot.slotId);
                    graph.Connect(outputSlotRef, inputSlotRef);
                }
                // one edge needs to go to outside world
                else if (outputRemapExists)
                {
                    inputEdgeNeedsRemap.Add(edge);
                }
                else if (inputRemapExists)
                {
                    outputEdgeNeedsRemap.Add(edge);
                }
            }

            // we do a grouping here as the same output can
            // point to multiple inputs
            var uniqueOutputs = outputEdgeNeedsRemap.GroupBy(edge => edge.outputSlot);
            var inputsNeedingConnection = new List<KeyValuePair<IEdge, IEdge>>();
            foreach (var group in uniqueOutputs)
            {
                var inputNode = graph.inputNode;
                var slotId = inputNode.AddSlot();

                var outputSlotRef = new SlotReference(inputNode.guid, slotId);

                foreach (var edge in group)
                {
                    var newEdge = graph.Connect(outputSlotRef, new SlotReference(nodeGuidMap[edge.inputSlot.nodeGuid], edge.inputSlot.slotId));
                    inputsNeedingConnection.Add(new KeyValuePair<IEdge, IEdge>(edge, newEdge));
                }
            }

            var uniqueInputs = inputEdgeNeedsRemap.GroupBy(edge => edge.inputSlot);
            var outputsNeedingConnection = new List<KeyValuePair<IEdge, IEdge>>();
            foreach (var group in uniqueInputs)
            {
                var outputNode = graph.outputNode;
                var slotId = outputNode.AddSlot();

                var inputSlotRef = new SlotReference(outputNode.guid, slotId);

                foreach (var edge in group)
                {
                    var newEdge = graph.Connect(new SlotReference(nodeGuidMap[edge.outputSlot.nodeGuid], edge.outputSlot.slotId), inputSlotRef);
                    outputsNeedingConnection.Add(new KeyValuePair<IEdge, IEdge>(edge, newEdge));
                }
            }

            File.WriteAllText(path, EditorJsonUtility.ToJson(graph));
            AssetDatabase.ImportAsset(path);

            var subGraph = AssetDatabase.LoadAssetAtPath(path, typeof(MaterialSubGraphAsset)) as MaterialSubGraphAsset;
            if (subGraph == null)
                return;

            var subGraphNode = new SubGraphNode();
            graphDataSource.AddNode(subGraphNode);
            subGraphNode.subGraphAsset = subGraph;

            foreach (var edgeMap in inputsNeedingConnection)
            {
                graphDataSource.graph.Connect(edgeMap.Key.outputSlot, new SlotReference(subGraphNode.guid, edgeMap.Value.outputSlot.slotId));
            }

            foreach (var edgeMap in outputsNeedingConnection)
            {
                graphDataSource.graph.Connect(new SlotReference(subGraphNode.guid, edgeMap.Value.inputSlot.slotId), edgeMap.Key.inputSlot);
            }

            var toDelete = graphDataSource.elements.Where(e => e.selected).OfType<MaterialNodePresenter>();
            graphDataSource.RemoveElements(toDelete, new List<GraphEdgePresenter>());
        }

        private void UpdateShaderSubGraphOnDisk(string path)
        {
            var graph = inMemoryAsset as SubGraph;
            if (graph == null)
                return;

            File.WriteAllText(path, EditorJsonUtility.ToJson(inMemoryAsset));
            AssetDatabase.ImportAsset(path);
        }

        private void UpdateShaderGraphOnDisk(string path)
        {
            var graph = inMemoryAsset as UnityEngine.MaterialGraph.MaterialGraph;
            if (graph == null)
                return;

            var masterNode = graph.masterNode;
            if (masterNode == null)
                return;

            List<PropertyGenerator.TextureInfo> configuredTextures;
            masterNode.GetFullShader(GenerationMode.ForReals, "NotNeeded", out configuredTextures);

            var shaderImporter = AssetImporter.GetAtPath(path) as ShaderImporter;
            if (shaderImporter == null)
                return;

            var textureNames = new List<string>();
            var textures = new List<Texture>();
            foreach (var textureInfo in configuredTextures.Where(
                         x => x.modifiable == TexturePropertyChunk.ModifiableState.Modifiable))
            {
                var texture = EditorUtility.InstanceIDToObject(textureInfo.textureId) as Texture;
                if (texture == null)
                    continue;
                textureNames.Add(textureInfo.name);
                textures.Add(texture);
            }
            shaderImporter.SetDefaultTextures(textureNames.ToArray(), textures.ToArray());

            textureNames.Clear();
            textures.Clear();
            foreach (var textureInfo in configuredTextures.Where(
                         x => x.modifiable == TexturePropertyChunk.ModifiableState.NonModifiable))
            {
                var texture = EditorUtility.InstanceIDToObject(textureInfo.textureId) as Texture;
                if (texture == null)
                    continue;
                textureNames.Add(textureInfo.name);
                textures.Add(texture);
            }
            shaderImporter.SetNonModifiableTextures(textureNames.ToArray(), textures.ToArray());
            File.WriteAllText(path, EditorJsonUtility.ToJson(inMemoryAsset));
            shaderImporter.SaveAndReimport();
            AssetDatabase.ImportAsset(path);
        }

        public virtual void ToggleRequiresTime()
        {
            allowAlwaysRepaint = !allowAlwaysRepaint;
        }

        public void ChangeSelection(Object newSelection)
        {
            if (!EditorUtility.IsPersistent(newSelection))
                return;

            if (selected == newSelection)
                return;

            if (selected != null)
            {
                if (EditorUtility.DisplayDialog("Save Old Graph?", "Save Old Graph?", "yes!", "no"))
                {
                    UpdateAsset();
                }
            }

            selected = newSelection;

            var path = AssetDatabase.GetAssetPath(newSelection);
            var textGraph = File.ReadAllText(path, Encoding.UTF8);
            inMemoryAsset = JsonUtility.FromJson<TGraphType>(textGraph);
            inMemoryAsset.OnEnable();
            inMemoryAsset.ValidateGraph();

            var source = CreateDataSource();
            source.Initialize(inMemoryAsset, this);
            m_GraphEditorView.presenter = source;

            titleContent = new GUIContent(selected.name);

            Repaint();
        }
    }
}
