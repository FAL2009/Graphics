using UnityEngine.Graphing;

namespace UnityEngine.MaterialGraph
{
    interface IMayRequireMeshUV
    {
        bool RequiresMeshUV(int index);
    }

    [Title("Input/UV Node")]
	public class UVNode : AbstractMaterialNode, IGeneratesBodyCode, IMayRequireMeshUV
    {
        public const int OutputSlotId = 0;
        private const string kOutputSlotName = "UV";

        public override bool hasPreview { get { return true; } }

        public UVNode()
        {
            name = "UV";
            UpdateNodeAfterDeserialization();
        }

        public override void UpdateNodeAfterDeserialization()
        {
            AddSlot(new MaterialSlot(OutputSlotId, kOutputSlotName, kOutputSlotName, SlotType.Output, SlotValueType.Vector4, Vector4.zero));
            RemoveSlotsNameNotMatching(new[] { OutputSlotId });
        }

        public void GenerateNodeCode(ShaderGenerator visitor, GenerationMode generationMode)
        {
            visitor.AddShaderChunk(precision + "4 " + GetVariableNameForSlot(OutputSlotId) + " = " + ShaderGeneratorNames.UV[0] + ";", true);
        }

        public bool RequiresMeshUV(int index)
        {
            return index == 0;
        }
    }
}
