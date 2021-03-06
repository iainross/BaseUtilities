﻿/*
 * Copyright © 2015 - 2018 EDDiscovery development team
 *
 * Licensed under the Apache License, Version 2.0 (the "License"); you may not use this
 * file except in compliance with the License. You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software distributed under
 * the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF
 * ANY KIND, either express or implied. See the License for the specific language
 * governing permissions and limitations under the License.
 *
 * EDDiscovery is not affiliated with Frontier Developments plc.
 */

using System;
using System.Diagnostics;
using OpenTKUtils;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using System.Windows.Forms;
using System.Drawing;

namespace OpenTKUtils.Common
{
    // class brings together keyboard, mouse, posdir, zoom to provide a means to move thru the playfield and zoom.
    // handles keyboard actions and mouse actions to provide a nice method of controlling the 3d playfield

    public class Controller3D
    {
        public OpenTK.GLControl glControl { get; private set; }      // use to draw to
        public float zNear { get; private set; }                     // model znear

        public Func<int,float> TravelSpeed;                       // set to scale travel speed given this time interval

        public Color BackColour { get; set; } = (Color)System.Drawing.ColorTranslator.FromHtml("#0D0D10");

        public Action<Matrix4,Matrix4, long> PaintObjects;  // madatory if you actually want to see anything

        public Action<MouseEventArgs> MouseDown;            // optional - set to handle more mouse actions if required
        public Action<MouseEventArgs> MouseUp;
        public Action<MouseEventArgs> MouseMove;
        public Action<MouseEventArgs> MouseWheel;

        public int LastHandleInterval;                      // set after handlekeyboard, how long since previous one was handled in ms

        public MatrixCalc MatrixCalc { get { return matrix; } }
        public Zoom Zoom { get { return zoom; } }
        public Position Pos { get { return pos; } }
        public Camera Camera { get { return camera; } }

        private Position pos = new Position();
        private Camera camera = new Camera();
        private MatrixCalc matrix = new MatrixCalc();
        private Zoom zoom = new Zoom();
        private Fov fov = new Fov();
        private BaseUtils.KeyboardState keyboard = new BaseUtils.KeyboardState();        // needed to be held because it remembers key downs
        private CameraDirectionMovementTracker cdmtracker = new CameraDirectionMovementTracker();        // these track movements and zoom for most systems

        private Stopwatch sysinterval = new Stopwatch();    // to accurately measure interval between system ticks
        private long lastintervalcount = 0;                   // last update tick at previous update

        private Point mouseDownPos;
        private Point mouseStartRotate = new Point(int.MinValue, int.MinValue);        // used to indicate not started for these using mousemove
        private Point mouseStartTranslateXZ = new Point(int.MinValue, int.MinValue);
        private Point mouseStartTranslateXY = new Point(int.MinValue, int.MinValue);

        public void CreateGLControl()
        {
            this.glControl = new GLControl();
            this.glControl.Dock = DockStyle.Fill;
            this.glControl.BackColor = System.Drawing.Color.Black;
            this.glControl.Name = "glControl";
            this.glControl.TabIndex = 0;
            this.glControl.VSync = true;
            this.glControl.MouseDown += new System.Windows.Forms.MouseEventHandler(this.glControl_MouseDown);
            this.glControl.MouseMove += new System.Windows.Forms.MouseEventHandler(this.glControl_MouseMove);
            this.glControl.MouseUp += new System.Windows.Forms.MouseEventHandler(this.glControl_MouseUp);
            this.glControl.MouseWheel += new System.Windows.Forms.MouseEventHandler(this.glControl_OnMouseWheel);
            this.glControl.Paint += new System.Windows.Forms.PaintEventHandler(this.glControl_Paint);
            this.glControl.KeyDown += GlControl_KeyDown;
            this.glControl.KeyUp += GlControl_KeyUp;
            this.glControl.Resize += GlControl_Resize;
        }

        private void GlControl_KeyUp(object sender, KeyEventArgs e)
        {
            keyboard.KeyUp(e.Control, e.Shift, e.Alt, e.KeyCode);
        }

        private void GlControl_KeyDown(object sender, KeyEventArgs e)
        {
            keyboard.KeyDown(e.Control, e.Shift, e.Alt, e.KeyCode);
        }

        public void Start(Vector3 lookat, Vector3 cameradir, float zoomn)
        {
            pos.Set(lookat);
            camera.Set(cameradir);
            zoom.Default = zoomn;
            zoom.SetDefault();

            cdmtracker.Update(camera.Current, pos.Current, this.zoom.Current, 1.0F); // set up here so ready for action.. below uses it.
            SetModelProjectionMatrix();

            GL.ClearColor(BackColour);

            sysinterval.Start();
        }

        public void KillSlews() { pos.KillSlew(); camera.KillSlew(); zoom.KillSlew(); }

        // misc

        public void Redraw() { glControl.Invalidate(); }

        #region Implementation

        private void GlControl_Resize(object sender, EventArgs e)           // there was a gate in the original around OnShown.. not sure why.
        {
            SetModelProjectionMatrix();
            glControl.Invalidate();
        }

        private void SetModelProjectionMatrix()
        {
            matrix.CalculateProjectionMatrix(fov.Current, glControl.Width, glControl.Height, out float zn);
            zNear = zn;
            matrix.CalculateModelMatrix(pos.Current, camera.Current, zoom.Current);
            GL.Viewport(0, 0, glControl.Width, glControl.Height);                        // Use all of the glControl painting area
        }

        private void glControl_Paint(object sender, PaintEventArgs e)
        {
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            GL.Enable(EnableCap.PointSmooth);                                               // standard render options
            GL.Hint(HintTarget.PointSmoothHint, HintMode.Nicest);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            PaintObjects?.Invoke(matrix.ModelMatrix, matrix.ProjectionMatrix, sysinterval.ElapsedMilliseconds);

            glControl.SwapBuffers();
        }

        // Owner should call this at regular intervals.
        // handle keyboard, indicate if activated, handle other keys if required, return movement calculated in case you need to use it

        public CameraDirectionMovementTracker HandleKeyboard( bool activated, Action<BaseUtils.KeyboardState> handleotherkeys = null)
        {
            long elapsed = sysinterval.ElapsedMilliseconds;         // stopwatch provides precision timing on last paint time.
            LastHandleInterval = (int)(elapsed - lastintervalcount);
            lastintervalcount = elapsed;

            if (activated && glControl.Focused)                      // if we can accept keys
            {
                if (StandardKeyboardHandler.Camera(keyboard, camera, LastHandleInterval))       // moving the camera around kills the pos slew (as well as its own slew)
                    pos.KillSlew();

                if (StandardKeyboardHandler.Movement(keyboard, pos, matrix.InPerspectiveMode, camera.Current, TravelSpeed != null ? TravelSpeed(LastHandleInterval) : 1.0f, true))
                    camera.KillSlew();              // moving the pos around kills the camera slew (as well as its own slew)

                StandardKeyboardHandler.Zoom(keyboard, zoom, LastHandleInterval);      // zoom slew is not affected by the above

                handleotherkeys?.Invoke(keyboard);
            }
            else
            {
                keyboard.Reset();
            }

            pos.DoSlew(LastHandleInterval);
            camera.DoSlew(LastHandleInterval);
            zoom.DoSlew();

            cdmtracker.Update(camera.Current, pos.Current, zoom.Current, 10.0F);       // Gross limit allows you not to repaint due to a small movement. I've set it to all the time for now, prefer the smoothness to the frame rate.

            if (cdmtracker.AnythingChanged)
            {
                matrix.CalculateModelMatrix(pos.Current, camera.Current, zoom.Current);
                //System.Diagnostics.Debug.WriteLine("Moved " + pos.Current + " " + camera.Current);
                glControl.Invalidate();
            }

            return cdmtracker;
        }

        private void glControl_MouseDown(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            KillSlews();

            mouseDownPos.X = e.X;
            mouseDownPos.Y = e.Y;

            if (e.Button.HasFlag(System.Windows.Forms.MouseButtons.Left))
            {
                mouseStartRotate.X = e.X;
                mouseStartRotate.Y = e.Y;
            }

            if (e.Button.HasFlag(System.Windows.Forms.MouseButtons.Right))
            {
                mouseStartTranslateXY.X = e.X;
                mouseStartTranslateXY.Y = e.Y;
                mouseStartTranslateXZ.X = e.X;
                mouseStartTranslateXZ.Y = e.Y;
            }

            MouseDown?.Invoke(e);
        }

        private void glControl_MouseUp(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            bool notmovedmouse = Math.Abs(e.X - mouseDownPos.X) + Math.Abs(e.Y - mouseDownPos.Y) < 4;

            if (!notmovedmouse)     // if we moved it, its not a stationary click, ignore
                return;


            if (e.Button == System.Windows.Forms.MouseButtons.Right)                    // right clicks are about bookmarks.
            {
                mouseStartTranslateXY = new Point(int.MinValue, int.MinValue);         // indicate rotation is finished.
                mouseStartTranslateXZ = new Point(int.MinValue, int.MinValue);
            }

            MouseUp?.Invoke(e);
        }

        private void glControl_MouseMove(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
            {
                if (mouseStartRotate.X != int.MinValue) // on resize double click resize, we get a stray mousemove with left, so we need to make sure we actually had a down event
                {
                    KillSlews();
                    int dx = e.X - mouseStartRotate.X;
                    int dy = e.Y - mouseStartRotate.Y;

                    mouseStartRotate.X = mouseStartTranslateXZ.X = e.X;
                    mouseStartRotate.Y = mouseStartTranslateXZ.Y = e.Y;
                    //System.Diagnostics.Trace.WriteLine("dx" + dx.ToString() + " dy " + dy.ToString() + " Button " + e.Button.ToString());

                    camera.Rotate(new Vector3((float)(-dy / 4.0f), (float)(dx / 4.0f), 0));
                }
            }
            else if (e.Button == System.Windows.Forms.MouseButtons.Right)
            {
                if (mouseStartTranslateXY.X != int.MinValue)
                {
                    KillSlews();

                    int dx = e.X - mouseStartTranslateXY.X;
                    int dy = e.Y - mouseStartTranslateXY.Y;

                    mouseStartTranslateXY.X = mouseStartTranslateXZ.X = e.X;
                    mouseStartTranslateXY.Y = mouseStartTranslateXZ.Y = e.Y;
                    //System.Diagnostics.Trace.WriteLine("dx" + dx.ToString() + " dy " + dy.ToString() + " Button " + e.Button.ToString());

                    pos.Translate(new Vector3(0, -dy * (1.0f / zoom.Current) * 2.0f, 0));
                }
            }
            else if (e.Button == (System.Windows.Forms.MouseButtons.Left | System.Windows.Forms.MouseButtons.Right))
            {
                if (mouseStartTranslateXZ.X != int.MinValue)
                {
                    KillSlews();

                    int dx = e.X - mouseStartTranslateXZ.X;
                    int dy = e.Y - mouseStartTranslateXZ.Y;

                    mouseStartTranslateXZ.X = mouseStartRotate.X = mouseStartTranslateXY.X = e.X;
                    mouseStartTranslateXZ.Y = mouseStartRotate.Y = mouseStartTranslateXY.Y = e.Y;
                    //System.Diagnostics.Trace.WriteLine("dx" + dx.ToString() + " dy " + dy.ToString() + " Button " + e.Button.ToString());

                    Matrix3 transform = Matrix3.CreateRotationZ((float)(-camera.Current.Y * Math.PI / 180.0f));
                    Vector3 translation = new Vector3(-dx * (1.0f / zoom.Current) * 2.0f, dy * (1.0f / zoom.Current) * 2.0f, 0.0f);
                    translation = Vector3.Transform(translation, transform);

                    pos.Translate(new Vector3(translation.X, 0, translation.Y));
                }
            }

            MouseMove?.Invoke(e);
        }

        private void glControl_OnMouseWheel(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            if (e.Delta != 0)
            {
                if (keyboard.Ctrl)
                {
                    if (fov.Scale(e.Delta < 0))
                    {
                        SetModelProjectionMatrix();
                        glControl.Invalidate();
                    }
                }
                else
                {
                    zoom.Scale(e.Delta > 0);
                }
            }

            MouseWheel?.Invoke(e);
        }

        #endregion
    }
}
