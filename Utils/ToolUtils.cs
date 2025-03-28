using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.UI;

namespace WinUIMusicPlayer.Utils
{
    public class ToolUtils
    {
        public enum PlayMode
        {
            SingleLoop,
            ListLoop,
            RandomLoop
        }
        public static Color InvertColor(Color color)
        {
            byte invertedR = (byte)(255 - color.R);
            byte invertedG = (byte)(255 - color.G);
            byte invertedB = (byte)(255 - color.B);
            // 保持透明度不变
            byte alpha = color.A;

            return Color.FromArgb(alpha, invertedR, invertedG, invertedB);
        }

        public static Color MakeColorTransparent(Color originalColor, byte alphaValue)
        {
            // 确保透明度值在 0-255 范围内
            alphaValue = alphaValue > 255 ? (byte)255 : (byte)alphaValue;
            alphaValue = alphaValue < 0 ? (byte)0 : alphaValue;

            return Color.FromArgb(alphaValue, originalColor.R, originalColor.G, originalColor.B);
        }

        public static Color ConvertToColorOffset(Color color,int offSet)
        {            
            byte invertedR = 0;
            byte invertedG = 0;
            byte invertedB = 0;
            if (color.G < 128)
            {
                invertedG = (byte)(color.G + offSet);
            }
            else {
                invertedG = (byte)(color.G - offSet);
            }
            if (color.B < 128)
            {
                invertedB = (byte)(color.B + offSet);
            }
            else
            {
                invertedB = (byte)(color.B - offSet);
            }
            if (color.R < 128)
            {
                invertedR = (byte)(color.R + offSet);
            }
            else
            {
                invertedR = (byte)(color.R - offSet);
            }
            System.Diagnostics.Debug.WriteLine("R:"+invertedR +"G:"+invertedG + "B:"+invertedB);
            return Color.FromArgb(color.A, invertedR, invertedG, invertedB);
        }
    }
}
