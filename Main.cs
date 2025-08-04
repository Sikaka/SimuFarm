using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.PoEMemory.Models;
using Microsoft.VisualBasic.Logging;
using System.Diagnostics;
namespace Agent
{
    public class Main : BaseSettingsPlugin<Settings>
    {
        bool isActive = false;
        private Sequences.SimulacrumFarmer farmer;

        public override bool Initialise()
        {
            this.Name = "Agent";
            Controls.Settings = this.Settings;
            Controls.Controller = this.GameController;
            farmer = new Sequences.SimulacrumFarmer(GameController, Graphics,Settings);
            return base.Initialise();
        }

        public override void AreaChange(AreaInstance area)
        {
            base.AreaChange(area);

            var newMap = new Navigation.Map(GameController, Settings);

            // Pass the new map context to our farmer instance
            if (farmer != null)
            {
                farmer.OnAreaChanged(newMap);
            }
        }

        public override Job Tick()
        {
            if (isActive && farmer != null)
            {
                farmer.Farm();
            }
            return base.Tick();
        }

        public override void Render()
        {
            base.Render();

            if (Settings.StartBot.PressedOnce())
            {
                isActive = !isActive;

                farmer.ResetForNewMap();
            }


            if (isActive && farmer != null)            
                farmer.Render();
            
        }

    }
}