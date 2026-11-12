using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace Alfred {
    static class ScreenGrab {
        public static string Folder {get{return Path.Combine(Program.Data,"screenshots");}}
        public static Rectangle Region(string part,Rectangle area){
            int x=area.X,y=area.Y,w=area.Width,h=area.Height,mx=w/2,my=h/2;
            switch(part){
                case "left":return new Rectangle(x,y,mx,h);
                case "right":return new Rectangle(x+mx,y,w-mx,h);
                case "top":return new Rectangle(x,y,w,my);
                case "bottom":return new Rectangle(x,y+my,w,h-my);
                case "top-left":return new Rectangle(x,y,mx,my);
                case "top-right":return new Rectangle(x+mx,y,w-mx,my);
                case "bottom-left":return new Rectangle(x,y+my,mx,h-my);
                case "bottom-right":return new Rectangle(x+mx,y+my,w-mx,h-my);
                case "center":return new Rectangle(x+w/4,y+h/4,w/2,h/2);
                default:return area;
            }
        }
        public static string Capture(Rectangle bounds){
            bounds=Rectangle.Intersect(bounds,SystemInformation.VirtualScreen);
            if(bounds.Width<2||bounds.Height<2)throw new Exception("স্ক্রিনশটের অংশটি পাওয়া যায়নি");
            if((long)bounds.Width*bounds.Height>70000000)throw new Exception("একবারে একটি মনিটরের স্ক্রিনশট নাও");
            Directory.CreateDirectory(Folder);string file=Path.Combine(Folder,"shot-"+DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff")+".png");
            using(Bitmap image=new Bitmap(bounds.Width,bounds.Height,PixelFormat.Format32bppArgb))using(Graphics g=Graphics.FromImage(image)){
                g.CopyFromScreen(bounds.Location,Point.Empty,bounds.Size,CopyPixelOperation.SourceCopy);image.Save(file,ImageFormat.Png);
            }return file;
        }
        public static string Latest(){if(!Directory.Exists(Folder))return null;string[] files=Directory.GetFiles(Folder,"shot-*.png");Array.Sort(files,StringComparer.Ordinal);return files.Length==0?null:files[files.Length-1];}
        public static string SelfTest(){
            Rectangle screen=new Rectangle(-1920,0,1921,1081);Rectangle left=Region("left",screen),right=Region("right",screen);
            if(left.Width+right.Width!=screen.Width||left.Right!=right.Left||right.Right!=screen.Right)throw new Exception("Screenshot half geometry failed");
            Rectangle q=Region("bottom-right",screen);if(q.Right!=screen.Right||q.Bottom!=screen.Bottom||q.X!=right.X)throw new Exception("Screenshot corner geometry failed");
            if(!Path.GetFullPath(Folder).StartsWith(Path.GetFullPath(Program.Root)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new Exception("Screenshot output escaped Alfred root");
            return "PASS screenshot halves/corners, multi-monitor coordinates and output root"+Environment.NewLine;
        }
    }
    sealed class RegionPicker : Form {
        readonly Action<Rectangle> done;
        Point start,end;bool drawing,completed;
        public RegionPicker(Action<Rectangle> callback){
            done=callback;FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;StartPosition=FormStartPosition.Manual;Bounds=SystemInformation.VirtualScreen;TopMost=true;BackColor=Color.Black;Opacity=0.30;Cursor=Cursors.Cross;DoubleBuffered=true;KeyPreview=true;
            Controls.Add(new Label{Text="অংশ বেছে নিতে মাউস টানো · বাতিল: Esc",AutoSize=true,Location=new Point(24,24),ForeColor=Color.White,Font=new Font("Nirmala UI",15)});
            MouseDown+=delegate(object s,MouseEventArgs e){if(e.Button==MouseButtons.Left){drawing=true;start=e.Location;end=e.Location;Capture=true;}};
            MouseMove+=delegate(object s,MouseEventArgs e){if(drawing){end=e.Location;Invalidate();}};
            MouseUp+=delegate(object s,MouseEventArgs e){if(!drawing)return;drawing=false;Capture=false;end=e.Location;Rectangle r=Selection();Rectangle selected=r.Width>=4&&r.Height>=4?new Rectangle(r.X+Left,r.Y+Top,r.Width,r.Height):Rectangle.Empty;completed=true;Close();done(selected);};
            KeyDown+=delegate(object s,KeyEventArgs e){if(e.KeyCode==Keys.Escape)Close();};
            FormClosed+=delegate{if(!completed)done(Rectangle.Empty);};
        }
        Rectangle Selection(){return new Rectangle(Math.Min(start.X,end.X),Math.Min(start.Y,end.Y),Math.Abs(start.X-end.X),Math.Abs(start.Y-end.Y));}
        protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);using(Pen p=new Pen(Color.Cyan,3))if(drawing)e.Graphics.DrawRectangle(p,Selection());}
    }
    sealed class SmallCalculation {
        string text;int at;
        SmallCalculation(string s){text=s;}
        void Spaces(){while(at<text.Length&&Char.IsWhiteSpace(text[at]))at++;}
        bool Take(char c){Spaces();if(at<text.Length&&text[at]==c){at++;return true;}return false;}
        double Sum(){double v=Product();while(true){if(Take('+'))v+=Product();else if(Take('-'))v-=Product();else return v;}}
        double Product(){double v=Atom();while(true){if(Take('*'))v*=Atom();else if(Take('/')){double n=Atom();if(n==0)throw new Exception("শূন্য দিয়ে ভাগ করা যায় না");v/=n;}else return v;}}
        double Atom(){Spaces();if(Take('+'))return Atom();if(Take('-'))return -Atom();double n;if(Take('(')){n=Sum();if(!Take(')'))throw new Exception("হিসাবের বন্ধনী মিলছে না");}else{Spaces();int begin=at;while(at<text.Length&&(Char.IsDigit(text[at])||text[at]=='.'))at++;if(begin==at||!Double.TryParse(text.Substring(begin,at-begin),System.Globalization.NumberStyles.AllowDecimalPoint,System.Globalization.CultureInfo.InvariantCulture,out n))throw new Exception("সংখ্যা ও যোগ/বিয়োগ/গুণ/ভাগ দিয়ে হিসাব বলো");}if(Take('%'))n/=100;return n;}
        public static double Evaluate(string raw){
            string s=raw;for(int n=0;n<10;n++)s=s.Replace((char)('০'+n),(char)('0'+n));s=s.Replace("যোগ","+").Replace("বিয়োগ","-").Replace("বিয়োগ","-").Replace("গুণ","*").Replace("গুন","*").Replace("ভাগ","/").Replace("শতাংশ","%");
            if(s.Length>300)throw new Exception("হিসাবটি একটু ছোট করে বলো");SmallCalculation p=new SmallCalculation(s);double value=p.Sum();p.Spaces();if(p.at!=s.Length||Double.IsNaN(value)||Double.IsInfinity(value))throw new Exception("হিসাবটি বুঝিনি");return value;
        }
        public static string SelfTest(){if(Evaluate("১২৫ গুণ ৮")!=1000||Evaluate("(2+3)*4")!=20||Evaluate("200*10%")!=20)throw new Exception("Calculation failed");bool blocked=false;try{Evaluate("1/0");}catch{blocked=true;}if(!blocked)throw new Exception("Division by zero accepted");return "PASS arithmetic precedence, Bengali digits, percent and zero-division"+Environment.NewLine;}
    }
}
