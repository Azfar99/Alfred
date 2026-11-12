using System;
using System.Drawing;
using System.Drawing.Drawing2D;
namespace Alfred {
    // Cached drawing resources; the same renderer drives the desktop orb and its preview.
    sealed class OrbVisual : IDisposable {
        const int Strands=24;
        readonly PointF[] points=new PointF[193];
        readonly Pen[] idle=new Pen[Strands],live=new Pen[Strands],dim=new Pen[Strands];
        readonly Pen haze=new Pen(Color.FromArgb(16,45,177,255),0.65f);
        readonly Pen orbit=new Pen(Color.FromArgb(48,65,164,224),0.09f);
        readonly Pen accent=new Pen(Color.FromArgb(160,131,236,255),0.15f);
        readonly SolidBrush hit=new SolidBrush(Color.FromArgb(1,0,0,0));
        readonly SolidBrush quietCore=new SolidBrush(Color.FromArgb(170,89,134,157));
        readonly SolidBrush white=new SolidBrush(Color.FromArgb(255,225,251,255));
        readonly SolidBrush cyan=new SolidBrush(Color.FromArgb(235,82,211,255));
        readonly SolidBrush[] halo=new SolidBrush[7];
        readonly Pen warning=new Pen(Color.FromArgb(185,255,166,94),0.16f);
        public OrbVisual(){
            for(int n=0;n<Strands;n++){
                double f=(double)n/(Strands-1);
                Color c=Color.FromArgb((int)(48+64*f),(int)(142+83*f),(int)(240+15*f));
                idle[n]=new Pen(Color.FromArgb(80+(n%4)*15,c),n%6==0?0.10f:0.047f);
                live[n]=new Pen(Color.FromArgb(125+(n%4)*22,c),n%6==0?0.13f:0.063f);
                dim[n]=new Pen(Color.FromArgb(40+(n%4)*8,c),0.075f);
            }
            for(int n=0;n<halo.Length;n++)halo[n]=new SolidBrush(Color.FromArgb(20-n*3,42,187,255));
            foreach(Pen p in live)p.LineJoin=LineJoin.Round;
        }
        public void Draw(Graphics g,double seconds,string phase,float voice,bool paused,bool highlighted){
            g.Clear(Color.Transparent);g.FillEllipse(hit,0,0,32,32);
            bool active=phase=="recording"||phase=="processing"||phase=="activating"||phase=="speaking";
            double t=seconds*(active?2.0:1.55);voice=active?Math.Max(0,Math.Min(1,voice)):0;
            // Small broken orbital highlights give the moving ribbons a clear direction.
            float spin=(float)(t*28%360);
            g.DrawArc(orbit,1.8f,1.8f,28.4f,28.4f,spin+25,56);
            g.DrawArc(orbit,1.8f,1.8f,28.4f,28.4f,spin+205,30);
            if(!paused)g.DrawArc(accent,1.8f,1.8f,28.4f,28.4f,spin+25,active?15:8);
            Pen[] pens=paused?dim:active||highlighted?live:idle;
            for(int n=0;n<Strands;n++){
                double layer=n*0.23;
                for(int j=0;j<points.Length;j++){
                    double a=j*Math.PI*2/(points.Length-1);
                    double weave=Math.Sin(a*3+layer+t*0.46)*1.32+Math.Sin(a*2-layer*0.75-t*0.62)*0.92;
                    double ripple=voice*Math.Sin(a*8+layer-t*8)*0.46;
                    float radius=(float)(10.15+weave*(0.76+voice*0.45)+voice*1.9+ripple);
                    radius=Math.Max(5.8f,Math.Min(15.0f,radius));
                    double rotation=t*0.24;
                    points[j]=new PointF(16+(float)Math.Cos(a+rotation)*radius,16+(float)Math.Sin(a+rotation)*radius);
                }
                if(active&&n%8==0)g.DrawLines(haze,points);
                g.DrawLines(pens[n],points);
            }
            if(active){
                float breathe=(float)(0.5+0.5*Math.Sin(t*3));
                for(int n=halo.Length-1;n>=0;n--){float size=0.55f+n*(0.25f+voice*0.10f)+breathe*0.10f;g.FillEllipse(halo[n],16-size,16-size,size*2,size*2);}
                float core=0.72f+voice*0.32f;g.FillEllipse(cyan,16-core,16-core,core*2,core*2);
                float point=0.37f+voice*0.13f;g.FillEllipse(white,16-point,16-point,point*2,point*2);
            }else g.FillEllipse(quietCore,15.6f,15.6f,0.8f,0.8f);
            if(phase=="error")g.DrawArc(warning,1,1,30,30,225,90);
        }
        public void Dispose(){foreach(Pen p in idle)p.Dispose();foreach(Pen p in live)p.Dispose();foreach(Pen p in dim)p.Dispose();foreach(SolidBrush b in halo)b.Dispose();haze.Dispose();orbit.Dispose();accent.Dispose();hit.Dispose();quietCore.Dispose();white.Dispose();cyan.Dispose();warning.Dispose();}
    }
}
