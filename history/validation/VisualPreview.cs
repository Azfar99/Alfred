using System;using System.Drawing;using System.Drawing.Drawing2D;using System.Drawing.Imaging;using Alfred;
class VisualPreview{
 static void Main(){using(OrbVisual orb=new OrbVisual())using(Bitmap image=new Bitmap(840,330))using(Graphics g=Graphics.FromImage(image))using(Font heading=new Font("Segoe UI",17,FontStyle.Bold))using(Font caption=new Font("Segoe UI",10)){
 g.SmoothingMode=SmoothingMode.AntiAlias;g.Clear(Color.FromArgb(10,17,28));
 using(Brush title=new SolidBrush(Color.FromArgb(224,240,251)))g.DrawString("ALFRED  /  BLUE ORBIT",heading,title,26,18);
 string[] names={"IDLE · flowing quietly","LISTENING · voice pulse","PROCESSING · bright nucleus"};string[] modes={"idle","recording","processing"};
 for(int n=0;n<3;n++){
  using(Brush card=new SolidBrush(Color.FromArgb(15,26,41)))g.FillRectangle(card,20+n*276,66,260,244);
  using(Bitmap tile=new Bitmap(202,202))using(Graphics tg=Graphics.FromImage(tile)){tg.SmoothingMode=SmoothingMode.AntiAlias;tg.ScaleTransform(6.3f,6.3f);orb.Draw(tg,2.4,modes[n],n==1?0.88f:0,false,false);g.DrawImageUnscaled(tile,50+n*276,70);}

  using(Brush text=new SolidBrush(Color.FromArgb(155,183,202)))g.DrawString(names[n],caption,text,35+n*276,278);
 }
 image.Save(@"F:\Alfred\build\orb-visual-preview.png",ImageFormat.Png);
 using(Bitmap frame=new Bitmap(160,160))using(Graphics fg=Graphics.FromImage(frame)){fg.SmoothingMode=SmoothingMode.AntiAlias;fg.ScaleTransform(5,5);orb.Draw(fg,2.4,"idle",0,false,false);frame.Save(@"F:\Alfred\build\orb-idle-a.png",ImageFormat.Png);orb.Draw(fg,3.2,"idle",0,false,false);frame.Save(@"F:\Alfred\build\orb-idle-b.png",ImageFormat.Png);}
 }}
}
