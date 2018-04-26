using UnityEditor.Experimental.UIElements.GraphView;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using System;

using BranchNew = UnityEditor.VFX.Operator.BranchNew;

namespace UnityEditor.VFX.UI
{
    class VFXOperatorController : VFXNodeController
    {
        protected override VFXDataAnchorController AddDataAnchor(VFXSlot slot, bool input, bool hidden)
        {
            VFXOperatorAnchorController anchor;
            if (input)
            {
                anchor = new VFXInputOperatorAnchorController(slot, this, hidden);
            }
            else
            {
                anchor = new VFXOutputOperatorAnchorController(slot, this, hidden);
            }

            anchor.portType = VFXOperatorAnchorController.GetDisplayAnchorType(slot);

            return anchor;
        }

        public VFXOperatorController(VFXModel model, VFXViewController viewController) : base(model, viewController)
        {
        }

        public new VFXOperator model
        {
            get
            {
                return base.model as VFXOperator;
            }
        }

        public virtual bool isEditable
        {
            get {return false; }
        }
    }

    class VFXVariableOperatorController : VFXOperatorController
    {
        public VFXVariableOperatorController(VFXModel model, VFXViewController viewController) : base(model, viewController)
        {
        }

        public new VFXOperatorDynamicOperand model
        {
            get
            {
                return base.model as VFXOperatorDynamicOperand;
            }
        }

        protected override bool CouldLinkMyInputTo(VFXDataAnchorController myInput, VFXDataAnchorController otherOutput)
        {
            if (otherOutput.direction == myInput.direction)
                return false;

            if (!myInput.CanLinkToNode(otherOutput.sourceNode))
                return false;
            return model.GetBestAffinityType(otherOutput.portType) != null;
        }

        public override bool isEditable
        {
            get {return true; }
        }
    }

    class VFXUnifiedOperatorControllerBase<T> : VFXVariableOperatorController where T : VFXOperatorNumericNew, IVFXOperatorNumericUnifiedNew
    {
        public VFXUnifiedOperatorControllerBase(VFXModel model, VFXViewController viewController) : base(model, viewController)
        {
        }

        public new T model
        {
            get
            {
                return base.model as T;
            }
        }
        public override void WillCreateLink(ref VFXSlot myInput, ref VFXSlot otherOutput)
        {
            if (!myInput.IsMasterSlot())
                return;
            var bestAffinityType = model.GetBestAffinityType(otherOutput.property.type);
            if (bestAffinityType != null)
            {
                int index = model.GetSlotIndex(myInput);
                model.SetOperandType(index, bestAffinityType);
                myInput = model.GetInputSlot(index);
            }
        }
    }
    class VFXUnifiedOperatorController : VFXUnifiedOperatorControllerBase<VFXOperatorNumericUnifiedNew>
    {
        public VFXUnifiedOperatorController(VFXModel model, VFXViewController viewController) : base(model, viewController)
        {
        }
    }
    class VFXUnifiedConstraintOperatorController : VFXUnifiedOperatorController
    {
        public VFXUnifiedConstraintOperatorController(VFXModel model, VFXViewController viewController) : base(model, viewController)
        {
        }

        protected override bool CouldLinkMyInputTo(VFXDataAnchorController myInput, VFXDataAnchorController otherOutput)
        {
            if (!myInput.model.IsMasterSlot())
                return false;
            if (otherOutput.direction == myInput.direction)
                return false;

            if (!myInput.CanLinkToNode(otherOutput.sourceNode))
                return false;

            int inputIndex = model.GetSlotIndex(myInput.model);
            IVFXOperatorNumericUnifiedConstrained constraintInterface = model as IVFXOperatorNumericUnifiedConstrained;


            var bestAffinityType = model.GetBestAffinityType(otherOutput.portType);
            if (bestAffinityType == null)
                return false;
            if (constraintInterface.allowExceptionalScalarSlotIndex.Contains(inputIndex))
            {
                if (VFXTypeUtility.GetComponentCount(otherOutput.model) != 0)  // If it is a vector or float type, then conversion to float exists
                    return true;
            }

            return model.GetBestAffinityType(otherOutput.portType) != null;
        }

        public static Type GetMatchingScalar(Type otherType)
        {
            return VFXExpression.GetMatchingScalar(otherType);
        }

        public override void WillCreateLink(ref VFXSlot myInput, ref VFXSlot otherOutput)
        {
            if (!myInput.IsMasterSlot())
                return;
            int inputIndex = model.GetSlotIndex(myInput);
            IVFXOperatorNumericUnifiedConstrained constraintInterface = model as IVFXOperatorNumericUnifiedConstrained;

            if (!constraintInterface.strictSameTypeSlotIndex.Contains(inputIndex))
            {
                base.WillCreateLink(ref myInput, ref otherOutput);
                return;
            }

            bool scalar = constraintInterface.allowExceptionalScalarSlotIndex.Contains(inputIndex);
            if (scalar)
            {
                var bestAffinityType = model.GetBestAffinityType(otherOutput.property.type);

                VFXSlot otherSlotWithConstraint = model.inputSlots.Where((t, i) => constraintInterface.strictSameTypeSlotIndex.Contains(i)).FirstOrDefault();

                if (otherSlotWithConstraint == null || otherSlotWithConstraint.property.type == bestAffinityType)
                {
                    model.SetOperandType(inputIndex, bestAffinityType);
                    myInput = model.GetInputSlot(inputIndex);
                }
                else if (!myInput.CanLink(otherOutput) || !otherOutput.CanLink(myInput))  // if the link is invalid if we don't change the type, change the type to the matching scalar
                {
                    var bestScalarAffinityType = model.GetBestAffinityType(GetMatchingScalar(otherOutput.property.type));
                    if (bestScalarAffinityType != null)
                    {
                        model.SetOperandType(inputIndex, bestScalarAffinityType);
                        myInput = model.GetInputSlot(inputIndex);
                    }
                }
                return; // never change the type of other constraint if the linked slot is scalar
            }

            VFXSlot input = myInput;

            bool hasLinks = model.inputSlots.Where((t, i) => t != input && t.HasLink(true) && constraintInterface.strictSameTypeSlotIndex.Contains(i) && !constraintInterface.allowExceptionalScalarSlotIndex.Contains(i)).Count() > 0;

            bool linkPossible = myInput.CanLink(otherOutput) && otherOutput.CanLink(myInput);

            if (!hasLinks || !linkPossible)  //Change the type if other type having the same constraint have no link or if the link will fail if we don't
            {
                var bestAffinityType = model.GetBestAffinityType(otherOutput.property.type);
                if (bestAffinityType != null)
                {
                    foreach (int slotIndex in constraintInterface.strictSameTypeSlotIndex)
                    {
                        if (!constraintInterface.allowExceptionalScalarSlotIndex.Contains(slotIndex) || GetMatchingScalar(bestAffinityType) != model.GetInputSlot(slotIndex).property.type)
                            model.SetOperandType(slotIndex, bestAffinityType);
                    }

                    myInput = model.GetInputSlot(inputIndex);
                }
            }
        }
    }

    class VFXCascadedOperatorController : VFXUnifiedOperatorControllerBase<VFXOperatorNumericCascadedUnifiedNew>
    {
        public VFXCascadedOperatorController(VFXModel model, VFXViewController viewController) : base(model, viewController)
        {
        }

        VFXUpcommingDataAnchorController m_UpcommingDataAnchor;
        protected override void NewInputSet(List<VFXDataAnchorController> newInputs)
        {
            if (m_UpcommingDataAnchor == null)
            {
                m_UpcommingDataAnchor = new VFXUpcommingDataAnchorController(this, false);
            }
            newInputs.Add(m_UpcommingDataAnchor);
        }

        public override void OnEdgeGoingToBeRemoved(VFXDataAnchorController myInput)
        {
            base.OnEdgeGoingToBeRemoved(myInput);

            RemoveOperand(myInput);
        }

        public bool CanRemove()
        {
            return model.operandCount > model.MinimalOperandCount;
        }

        public void RemoveOperand(VFXDataAnchorController myInput)
        {
            RemoveOperand(model.GetSlotIndex(myInput.model));
        }

        public void RemoveOperand(int index)
        {
            if (CanRemove())
            {
                model.RemoveOperand(index);
            }
        }
    }

    class VFXUniformOperatorController<T> : VFXVariableOperatorController where T : VFXOperatorDynamicOperand, IVFXOperatorUniform
    {
        public VFXUniformOperatorController(VFXModel model, VFXViewController viewController) : base(model, viewController)
        {
        }

        public new T model
        {
            get
            {
                return base.model as T;
            }
        }

        public override void WillCreateLink(ref VFXSlot myInput, ref VFXSlot otherOutput)
        {
            if (!myInput.IsMasterSlot())
                return;
            //Since every input will change at the same time the metric to change is :
            // if we have no input links yet

            var myInputCopy = myInput;
            bool hasLink = inputPorts.Any(t => t.model != myInputCopy && t.model.HasLink());
            int index = model.GetSlotIndex(myInput);

            if (model.staticSlotIndex.Contains(index))
                return;

            // The new link is impossible if we don't change (case of a vector3 trying to be linked to a vector4)
            bool linkImpossibleNow = !myInput.CanLink(otherOutput) || !otherOutput.CanLink(myInput);

            var bestAffinity = model.GetBestAffinityType(otherOutput.property.type);
            if ((!hasLink || linkImpossibleNow) && bestAffinity != null)
            {
                model.SetOperandType(bestAffinity);
                myInput = model.GetInputSlot(index);
            }
        }
    }

    class VFXNumericUniformOperatorController : VFXUniformOperatorController<VFXOperatorNumericUniformNew>
    {
        public VFXNumericUniformOperatorController(VFXModel model, VFXViewController viewController) : base(model, viewController)
        {
        }
    }

    class VFXBranchOperatorController : VFXUniformOperatorController<BranchNew>
    {
        public VFXBranchOperatorController(VFXModel model, VFXViewController viewController) : base(model, viewController)
        {
        }
    }
}
