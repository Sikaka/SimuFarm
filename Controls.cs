using ExileCore;
using System.Numerics;

namespace Agent
{
    public static class Controls
    {
        private static Random random = new Random();
        public static GameController Controller;
        public static Settings Settings;

        public static Vector2 GetScreenByWorldPos(Vector3 worldPos)
        {
            return Controller.IngameState.Camera.WorldToScreen(worldPos);
        }

        public static Vector2 GetScreenByGridPos(Vector2 gridPosNum)
        {
            return Controls.GetScreenByWorldPos(Controls.Controller.Game.IngameState.Data.ToWorldWithTerrainHeight(gridPosNum));
        }

        public static Vector2 GetScreenClampedGridPos(Vector2 gridPosNum)
        {
            var screenByGridPos = GetScreenByGridPos(gridPosNum);
            var windowRectangle = Controller.Window.GetWindowRectangle();
            windowRectangle.Height -= 130f;
            windowRectangle.Width -= 20f;
            windowRectangle.Y += 10f;
            windowRectangle.X += 10f;
            if (windowRectangle.Contains(new SharpDX.Vector2(screenByGridPos.X, screenByGridPos.Y)))
                return screenByGridPos;
            Vector2 vector2_1 = new Vector2(windowRectangle.Width / 2f, windowRectangle.Height / 2f);
            Vector2 vector2_2 = Vector2.Normalize(screenByGridPos - vector2_1);
            return vector2_1 + vector2_2 * (float)(int)Controls.Settings.ClampSize;
        }

        public static async Task ClickScreenPos(Vector2 position, bool isLeft = true, bool exactPosition = false, bool holdCtrl = false)
        {
            if (!exactPosition)
                position += new Vector2((float)random.Next(-15, 15), (float)random.Next(-15, 15));

            Input.SetCursorPos(position);
            await Task.Delay(random.Next(20, 50));

            if (holdCtrl)
            {
                //Confirm ctrl is not already pressed down.
                if (Input.IsKeyDown(Keys.LControlKey))
                    Input.KeyUp(Keys.LControlKey);


                Input.KeyDown(Keys.LControlKey); 
                await Task.Delay(random.Next(20, 50));
            }

            //Make sure ctrl is not pressed.
            else if (Input.IsKeyDown(Keys.LControlKey))
                Input.KeyUp(Keys.LControlKey);

            if (isLeft)
                await LeftClick();
            else
                await RightClick();

            if (holdCtrl)
                Input.KeyUp(Keys.LControlKey);
        }

        public static async Task UseKeyAtGridPos(Vector2 pos, Keys key, bool exactPosition = false)
        {
            var screenClampedGridPos = GetScreenClampedGridPos(pos);
            if (!exactPosition)
                screenClampedGridPos += new Vector2((float)random.Next(-5, 5), (float)random.Next(-5, 5));

            Input.SetCursorPos(screenClampedGridPos);
            await Task.Delay(random.Next(15, 30));
            await UseKey(key);
        }

        public static async Task UseKey(Keys key, int minDelay = 0)
        {
            Input.KeyDown(key);
            await Task.Delay(minDelay + random.Next(15, 30));
            Input.KeyUp(key);
        }

        public static async Task RightClick()
        {
            Input.RightDown();
            await Task.Delay(random.Next(10, 50));
            Input.RightUp();
        }
        public static async Task LeftClick()
        {
            Input.LeftDown();
            await Task.Delay(random.Next(10, 50));
            Input.LeftUp();
        }
        public static async Task SendChatMessage(string message)
        {            
            await Controls.UseKey(Keys.Enter);
            await Task.Delay(150); 
            string sanitizedMessage = message.Replace("+", "{+}")
                                             .Replace("^", "{^}")
                                             .Replace("%", "{%}")
                                             .Replace("~", "{~}")
                                             .Replace("(", "{(}")
                                             .Replace(")", "{)}")
                                             .Replace("{", "{{}")
                                             .Replace("}", "{}}");
            SendKeys.SendWait(sanitizedMessage);
            await Task.Delay(150);

            await Controls.UseKey(Keys.Enter);
            await Task.Delay(100);
        }
    }
}
