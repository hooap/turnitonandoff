using System;

using System.Collections.Generic;

using UnityEngine;
using Verse;
using RimWorld;

namespace TurnItOnandOff
{
    public class TurnItOnandOffDef : Def {
        public string targetDef;
        public int lowPower;
        public int highPower;
        public bool poweredWorkbench;
        public bool poweredReservable;
    }
}
