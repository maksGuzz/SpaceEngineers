using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VRageMath;

namespace Sandbox.ModAPI.Ingame
{
    public interface IMyShipConnector:IMyFunctionalBlock
    {
        bool ThrowOut { get; }
        bool CollectAll { get; }
        bool IsLocked { get; }
        bool IsConnected { get; }
        IMyShipConnector OtherConnector { get; }
        Vector3D PositionWorld { get; }
        Vector3D ConstraintWorld { get; }
    }
}
