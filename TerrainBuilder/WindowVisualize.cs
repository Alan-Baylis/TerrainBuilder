﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using OpenTK.Input;
using TerrainBuilder.RenderUtil;
using TerrainBuilder.RenderUtil.BmFont;
using TerrainBuilder.RenderUtil.Buffer;
using TerrainBuilder.RenderUtil.Shader;
using TerrainBuilder.WorldGen;
using TerrainGenCore;

namespace TerrainBuilder
{
    public class WindowVisualize : GameWindow
    {
        /*
         * Constants
         */
        public static Vector3 UpVector = Vector3.UnitY;
        public static Vector3 PosXVector = Vector3.UnitX;
        public static Vector3 NegXVector = -PosXVector;
        public static Vector3 PosZVector = Vector3.UnitZ;
        public static Vector3 NegZVector = -PosZVector;

        private static readonly Vector3[] _boxNormals = {
            new Vector3(-1.0f, 0.0f, 0.0f),
            new Vector3(0.0f, 1.0f, 0.0f),
            new Vector3(1.0f, 0.0f, 0.0f),
            new Vector3(0.0f, -1.0f, 0.0f),
            new Vector3(0.0f, 0.0f, 1.0f),
            new Vector3(0.0f, 0.0f, -1.0f)
        };

        private static readonly int[,] _boxFaces =
        {
            {0, 1, 2, 3},
            {3, 2, 6, 7},
            {7, 6, 5, 4},
            {4, 5, 1, 0},
            {5, 6, 2, 1},
            {7, 4, 0, 3}
        };

        private static readonly Vector3[] _boxVerts =
        {
            new Vector3(-0.5f, -0.5f, -0.5f),
            new Vector3(-0.5f, -0.5f, 0.5f),
            new Vector3(-0.5f, 0.5f, 0.5f),
            new Vector3(-0.5f, 0.5f,-0.5f),
            new Vector3(0.5f, -0.5f, -0.5f),
            new Vector3(0.5f, -0.5f, 0.5f),
            new Vector3(0.5f, 0.5f, 0.5f),
            new Vector3(0.5f, 0.5f, -0.5f)
        };

        /*
         * Script-related
         */
        private bool _dirty;
        public CsTerrainGenerator ScriptedTerrainGenerator = new CsTerrainGenerator();
        private readonly ScriptWatcher _scriptWatcher = new ScriptWatcher();

        /*
         * Render-related
         */
        private float _zoom = 1;
        private double _yaw = 45;
        private double _pitch = 160;
        private Vector3 _translation = new Vector3(0, -25, 0);

        private bool _voxels = true;

        private static ShaderProgram _shaderProgram;
        private static readonly List<Uniform> Uniforms = new List<Uniform>();
        private static readonly Uniform TintUniform = new Uniform("tint");

        private readonly SimpleVertexBuffer _terrainVbo = new SimpleVertexBuffer();
        private readonly BackgroundWorker _backgroundRenderer = new BackgroundWorker();

        private readonly SimpleVertexBuffer _decorVbo = new SimpleVertexBuffer();
        private readonly BackgroundWorker _backgroundDecorator = new BackgroundWorker();

        private readonly Stopwatch _renderStopwatch = new Stopwatch();

        /*
         * Terrain-related
         */
        private int _numVerts;
        private Color _tintColor;
        private Vector3 _tintColorVector;

        public int SideLength { get; set; } = 64;

        public Color TintColor
        {
            get => _tintColor;
            set
            {
                _tintColor = value;
                _tintColorVector = new Vector3(value.R / 255f, value.G / 255f, value.B / 255f);
            }
        }

        public static double[,] Heightmap;
        private readonly RenderController _terrainLayerList;

        /*
         * Window-related
         */
        private bool _shouldDie;
        private static KeyboardState _keyboard;
        private static BitmapFont _font;

        public WindowVisualize() : base(800, 600)
        {
            // Wire up window
            Load += LoadHandler;
            Closing += CloseHandler;
            Resize += ResizeHandler;
            UpdateFrame += UpdateHandler;
            RenderFrame += RenderHandler;
            MouseWheel += WindowVisualize_MouseWheel;
            MouseMove += WindowVisualize_MouseMove;

            // Wire up background worker
            _backgroundRenderer.WorkerReportsProgress = true;
            _backgroundRenderer.WorkerSupportsCancellation = true;
            _backgroundRenderer.DoWork += DoBackgroundRender;
            _backgroundRenderer.ProgressChanged += DoBackgroundRenderProgress;
            _backgroundRenderer.RunWorkerCompleted += DoBackgroundRenderComplete;

            // Wire up background worker
            _backgroundDecorator.WorkerReportsProgress = true;
            _backgroundDecorator.WorkerSupportsCancellation = true;
            _backgroundDecorator.DoWork += DoBackgroundDecorate;
            _backgroundDecorator.ProgressChanged += DoBackgroundDecorateProgress;
            _backgroundDecorator.RunWorkerCompleted += DoBackgroundDecorateComplete;

            // Wire up file watcher
            _scriptWatcher.FileChanged += ScriptWatcherOnFileChanged;

            TintColor = Color.LimeGreen;

            // Load UI window
            _terrainLayerList = new RenderController(this);
            _terrainLayerList.Show();
            Title = $"{EmbeddedFiles.AppName} | {EmbeddedFiles.Title_Unsaved}";
            Icon = EmbeddedFiles.logo;
        }

        private void LoadHandler(object sender, EventArgs e)
        {
            // Set up lights
            const float diffuse = 0.9f;
            float[] matDiffuse = { diffuse, diffuse, diffuse };
            GL.Material(MaterialFace.FrontAndBack, MaterialParameter.Diffuse, matDiffuse);
            GL.Light(LightName.Light0, LightParameter.Position, new[] { 0.0f, 0.0f, 0.0f, 10.0f });
            GL.Light(LightName.Light0, LightParameter.Diffuse, new[] { diffuse, diffuse, diffuse, diffuse });

            // Set up lighting
            GL.Enable(EnableCap.Lighting);
            GL.Enable(EnableCap.Light0);
            GL.ShadeModel(ShadingModel.Smooth);
            GL.Enable(EnableCap.ColorMaterial);

            // Set up caps
            GL.Enable(EnableCap.DepthTest);
            GL.Enable(EnableCap.RescaleNormal);

            // Set up blending
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            // Set background color
            GL.ClearColor(Color.FromArgb(255, 13, 13, 13));

            // Load fonts
            _font = BitmapFont.LoadBinaryFont("dina", EmbeddedFiles.dina_fnt, EmbeddedFiles.dina_bmp);

            // Init keyboard to ensure first frame won't NPE
            _keyboard = Keyboard.GetState();

            // Load shadersyeah
            _shaderProgram = new DefaultShaderProgram(EmbeddedFiles.default_fs);
            _shaderProgram.InitProgram();

            Lumberjack.Info(EmbeddedFiles.Info_WindowLoaded);
        }

        private void WindowVisualize_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            _zoom -= e.DeltaPrecise / 4f;

            if (_zoom < 0.5f)
                _zoom = 0.5f;
            if (_zoom > 35)
                _zoom = 35;
        }

        private void WindowVisualize_MouseMove(object sender, MouseMoveEventArgs e)
        {
            if (!e.Mouse.IsButtonDown(MouseButton.Left)) return;

            if (_keyboard[Key.ShiftLeft])
            {
                _translation.X += e.XDelta / 2f;
                _translation.Y -= e.YDelta / 2f;
            }
            else
            {
                _yaw -= e.XDelta / 2f;
                _pitch -= e.YDelta / 2f;
            }
        }

        private void CloseHandler(object sender, CancelEventArgs e)
        {
            if (!_shouldDie)
                _terrainLayerList?.Close();
        }

        public void Kill()
        {
            _shouldDie = true;
        }

        private void ResizeHandler(object sender, EventArgs e)
        {
            GL.Viewport(ClientRectangle);

            var aspectRatio = Width / (float)Height;
            var projection = Matrix4.CreatePerspectiveFieldOfView(MathHelper.PiOver4, aspectRatio, 1, 1024);
            GL.MatrixMode(MatrixMode.Projection);
            GL.LoadMatrix(ref projection);
        }

        public void WatchTerrainScript(string filename)
        {
            _scriptWatcher.WatchTerrainScript(filename);
        }

        private void ScriptWatcherOnFileChanged(object sender, ScriptChangedEventArgs e)
        {
            Lumberjack.Info(string.Format(EmbeddedFiles.Info_FileReloaded, e.Filename));
            _dirty = ScriptedTerrainGenerator.LoadScript(e.ScriptCode);
        }

        public bool IsRendering()
        {
            return _backgroundRenderer.IsBusy;
        }

        public void CancelRender()
        {
            Lumberjack.Warn(EmbeddedFiles.Info_CancellingPreviousRenderOp);
            _backgroundRenderer.CancelAsync();

            while (IsRendering())
                Application.DoEvents();
        }

        public void CancelBackgroundTasks()
        {
            if (IsRendering())
                CancelRender();
            if (IsDecorating())
                CancelDecorate();
        }

        public void ReRender(bool manualOverride = false, bool regenHeightmap = true)
        {
            // Make sure the render requirements are met
            if (_terrainLayerList == null || _terrainLayerList.cbPauseGen.Checked && !manualOverride)
                return;

            // If there's an ongoing render, cancel it
            if (IsRendering())
                CancelRender();

            // Enable the render statusbar
            _terrainLayerList.bCancelRender.Enabled = true;
            _terrainLayerList.bCancelRender.Visible = true;

            _terrainLayerList.pbRenderStatus.Visible = true;

            _renderStopwatch.Reset();
            _renderStopwatch.Start();

            // Fire up the render
            _backgroundRenderer.RunWorkerAsync(new BackgroundRenderArgs(_voxels, regenHeightmap));
        }

        private void DoBackgroundRenderProgress(object sender, ProgressChangedEventArgs progressChangedEventArgs)
        {
            // Render thread says something
            _terrainLayerList.Invoke((MethodInvoker)delegate
           {
               // Invoke changes on form thread
               _terrainLayerList.pbRenderStatus.Value = progressChangedEventArgs.ProgressPercentage;
               if (progressChangedEventArgs.UserState is string s)
                   _terrainLayerList.lRenderStatus.Text = s;
           });
        }

        private void DoBackgroundRenderComplete(object sender, RunWorkerCompletedEventArgs e)
        {
            // Render done, reset statusbar 
            _terrainLayerList.Invoke((MethodInvoker)delegate
           {
               _terrainLayerList.bCancelRender.Visible = false;
               _terrainLayerList.bCancelRender.Enabled = false;

               _terrainLayerList.pbRenderStatus.Visible = false;
               _terrainLayerList.pbRenderStatus.Value = 0;

               _terrainLayerList.lRenderStatus.Text = EmbeddedFiles.Status_Ready;
           });

            // If the render was manually cancelled, go no further
            if (_scriptWatcher.GetScriptId() == 0 || e.Cancelled || e.Result is null)
            {
                _renderStopwatch.Stop();
                Lumberjack.Log($"Render cancelled after {_renderStopwatch.Elapsed}");
                return;
            }

            // Take the render result and upload it to the VBO
            var result = (VertexBufferInitializer)e.Result;
            _numVerts = result.Vertices.Count;
            _terrainVbo.InitializeVbo(result);

            // Wait for render thread to exit
            while (IsRendering())
                Application.DoEvents();

            // Add chunk decor
            //ReDecorate();

            _renderStopwatch.Stop();
            Lumberjack.Log($"Render finished after {_renderStopwatch.Elapsed}");
        }

        private void DoBackgroundRender(object sender, DoWorkEventArgs e)
        {
            try
            {
                // Make sure the render requirements are met
                if (_scriptWatcher.GetScriptId() == 0)
                {
                    Lumberjack.Warn("Can't render, no terrain loaded.");
                    return;
                }

                // Grab worker and report progress
                var worker = (BackgroundWorker)sender;
                var args = (BackgroundRenderArgs)e.Argument;

                if (args.RegenHeightmap)
                {
                    worker.ReportProgress(0, EmbeddedFiles.Status_GenHeightmap);

                    // Init a new heightmap
                    Heightmap = new double[2 * SideLength + 2, 2 * SideLength + 2];

                    var done = 0;
                    Parallel.For(0, 2 * SideLength + 2, (x, state) =>
                    {
                        for (var z = 0; z < 2 * SideLength + 2; z++)
                        {
                            // Cancel if requested
                            if (worker.CancellationPending)
                            {
                                e.Cancel = true;
                                state.Stop();
                            }

                            // Set the heightmap at (x, z)
                            Heightmap[x, z] = (int)ScriptedTerrainGenerator.GetValue(x - SideLength, z - SideLength);
                        }

                        done++;
                        worker.ReportProgress((int)(done / (2f * SideLength + 2) * 50));
                    });
                }

                // Report progress with a new status message
                worker.ReportProgress(50, EmbeddedFiles.Status_UploadVBO);

                // Init VBO-needed lists
                var vbi = new VertexBufferInitializer();

                // Set up the colors we'll be using for terrain
                const int color = 0xFFFFFF;
                const float occludedScalar = 0.75f;
                var occludedColor = Util.RgbToInt(occludedScalar, occludedScalar, occludedScalar);

                // Iterate over the heightmap
                for (var x = -SideLength; x < SideLength; x++)
                {
                    for (var z = -SideLength; z < SideLength; z++)
                    {
                        // Cancel if requested
                        if (worker.CancellationPending)
                        {
                            e.Cancel = true;
                            return;
                        }

                        // Get the current internal array position
                        var nx = x + SideLength + 1;
                        var nz = z + SideLength + 1;

                        // Heightmap value here
                        var valueHere = (float)Heightmap[nx, nz];

                        // Neighboring positions
                        var valuePosX = Heightmap[nx + 1, nz];
                        var valueNegX = Heightmap[nx - 1, nz];
                        var valuePosZ = Heightmap[nx, nz + 1];
                        var valueNegZ = Heightmap[nx, nz - 1];

                        var valuePosXPosZ = Heightmap[nx + 1, nz + 1];
                        var valueNegXPosZ = Heightmap[nx - 1, nz + 1];
                        var valuePosXNegZ = Heightmap[nx + 1, nz - 1];
                        var valueNegXNegZ = Heightmap[nx - 1, nz - 1];

                        // Comparisons used in cheap AO
                        var isPosXHigher = valuePosX > valueHere;
                        var isNegXHigher = valueNegX > valueHere;
                        var isPosZHigher = valuePosZ > valueHere;
                        var isNegZHigher = valueNegZ > valueHere;

                        var isPosXLower = valuePosX < valueHere;
                        var isNegXLower = valueNegX < valueHere;
                        var isPosZLower = valuePosZ < valueHere;
                        var isNegZLower = valueNegZ < valueHere;

                        var isPosXPosZHigher = valuePosXPosZ > valueHere;
                        var isNegXPosZHigher = valueNegXPosZ > valueHere;
                        var isPosXNegZHigher = valuePosXNegZ > valueHere;
                        var isNegXNegZHigher = valueNegXNegZ > valueHere;

                        var isPosXPosZLower = valuePosXPosZ < valueHere;
                        var isNegXPosZLower = valueNegXPosZ < valueHere;
                        var isPosXNegZLower = valuePosXNegZ < valueHere;
                        var isNegXNegZLower = valueNegXNegZ < valueHere;

                        if (args.Voxels)
                        {
                            // Always draw a top face for a block
                            vbi.AddVertex(
                                new Vector3(x, valueHere, z),
                                UpVector,
                                isPosXHigher || isPosZHigher || isPosXPosZHigher ? occludedColor : color
                            );

                            vbi.AddVertex(
                                new Vector3(x - 1, valueHere, z),
                                UpVector,
                                isNegXHigher || isPosZHigher || isNegXPosZHigher ? occludedColor : color
                            );

                            vbi.AddVertex(
                                new Vector3(x - 1, valueHere, z - 1),
                                UpVector,
                                isNegXHigher || isNegZHigher || isNegXNegZHigher ? occludedColor : color
                            );

                            vbi.AddVertex(
                                new Vector3(x, valueHere, z - 1),
                                UpVector,
                                isPosXHigher || isNegZHigher || isPosXNegZHigher ? occludedColor : color
                            );

                            // Try and draw the PosZ face
                            if (valuePosZ < valueHere)
                            {
                                vbi.AddVertex(
                                    new Vector3(x, valueHere, z),
                                    PosZVector,
                                    color
                                );

                                vbi.AddVertex(
                                    new Vector3(x, (float)valuePosZ, z),
                                    PosZVector,
                                    isPosXLower || isPosZLower || isPosXPosZLower ? occludedColor : color
                                );

                                vbi.AddVertex(
                                    new Vector3(x - 1, (float)valuePosZ, z),
                                    PosZVector,
                                    isNegXLower || isPosZLower || isNegXPosZLower ? occludedColor : color
                                );

                                vbi.AddVertex(
                                    new Vector3(x - 1, valueHere, z),
                                    PosZVector,
                                    color
                                );
                            }

                            // Try and draw the NegZ face
                            if (valueNegZ < valueHere)
                            {
                                vbi.AddVertex(
                                    new Vector3(x, valueHere, z - 1),
                                    NegZVector,
                                    color
                                );

                                vbi.AddVertex(
                                    new Vector3(x, (float)valueNegZ, z - 1),
                                    NegZVector,
                                    isPosXLower || isNegZLower || isPosXNegZLower ? occludedColor : color
                                );

                                vbi.AddVertex(
                                    new Vector3(x - 1, (float)valueNegZ, z - 1),
                                    NegZVector,
                                    isNegXLower || isNegZLower || isNegXNegZLower ? occludedColor : color
                                );

                                vbi.AddVertex(
                                    new Vector3(x - 1, valueHere, z - 1),
                                    NegZVector,
                                    color
                                );
                            }

                            // Try and draw the PosX face
                            if (valuePosX < valueHere)
                            {
                                vbi.AddVertex(
                                    new Vector3(x, valueHere, z),
                                    PosXVector,
                                    color
                                );

                                vbi.AddVertex(
                                    new Vector3(x, (float)valuePosX, z),
                                    PosXVector,
                                    isPosXLower || isPosZLower || isPosXPosZLower ? occludedColor : color
                                );

                                vbi.AddVertex(
                                    new Vector3(x, (float)valuePosX, z - 1),
                                    PosXVector,
                                    isPosXLower || isNegZLower || isPosXNegZLower ? occludedColor : color
                                );

                                vbi.AddVertex(
                                    new Vector3(x, valueHere, z - 1),
                                    PosXVector,
                                    color
                                );
                            }

                            // Try and draw the NegX face
                            if (valueNegX < valueHere)
                            {
                                vbi.AddVertex(
                                    new Vector3(x - 1, valueHere, z),
                                    NegXVector,
                                    color
                                );

                                vbi.AddVertex(
                                    new Vector3(x - 1, (float)valueNegX, z),
                                    NegXVector,
                                    isNegXLower || isPosZLower || isNegXPosZLower ? occludedColor : color
                                );

                                vbi.AddVertex(
                                    new Vector3(x - 1, (float)valueNegX, z - 1),
                                    NegXVector,
                                    isNegXLower || isNegZLower || isNegXNegZLower ? occludedColor : color
                                );

                                vbi.AddVertex(
                                    new Vector3(x - 1, valueHere, z - 1),
                                    NegXVector,
                                    color
                                );
                            }
                        }
                        else
                        {
                            var valueA = new Vector3(x, valueHere, z);
                            var valueB = new Vector3(x - 1, (float)valueNegX, z);
                            var valueC = new Vector3(x, (float)valueNegZ, z - 1);
                            var valueD = new Vector3(x - 1, (float)valueNegXNegZ, z - 1);

                            var dirA = Vector3.Cross(valueB - valueA, valueD - valueA);
                            var normA = Vector3.Normalize(dirA);
                            var norm = -normA;

                            vbi.AddVertex(
                                valueA,
                                norm,
                                color
                            );

                            vbi.AddVertex(
                                valueB,
                                norm,
                                color
                            );

                            vbi.AddVertex(
                                valueD,
                                norm,
                                color
                            );

                            vbi.AddVertex(
                                valueC,
                                norm,
                                color
                            );
                        }
                    }

                    // Report progress every "scanline"
                    worker.ReportProgress((int)((x + SideLength) / (SideLength * 2f) * 50) + 50);
                }

                // Send the result back to the worker
                e.Result = vbi;
            }
            catch (Exception ex)
            {
                Lumberjack.Error(ex.Message);
                e.Result = null;
            }
        }

        public bool IsDecorating()
        {
            return _backgroundDecorator.IsBusy;
        }

        public void CancelDecorate()
        {
            Lumberjack.Warn(EmbeddedFiles.Info_CancellingPreviousRenderOp);
            _backgroundDecorator.CancelAsync();

            while (IsDecorating())
                Application.DoEvents();
        }

        private void DoBackgroundDecorateProgress(object sender, ProgressChangedEventArgs progressChangedEventArgs)
        {
            // Decorate thread says something
            _terrainLayerList.Invoke((MethodInvoker)delegate
           {
               // Invoke changes on form thread
               _terrainLayerList.pbRenderStatus.Value = progressChangedEventArgs.ProgressPercentage;
               if (progressChangedEventArgs.UserState is string s)
                   _terrainLayerList.lRenderStatus.Text = s;
           });
        }

        private void DoBackgroundDecorate(object sender, DoWorkEventArgs e)
        {
            var vbi = new VertexBufferInitializer();

            // Grab worker and report progress
            var worker = (BackgroundWorker)sender;
            worker.ReportProgress(0, "Decorating");

            for (var x = 0; x < 2 * SideLength + 2; x++)
            {
                for (var z = 0; z < 2 * SideLength + 2; z++)
                {
                    // Cancel if requested
                    if (worker.CancellationPending)
                    {
                        e.Cancel = true;
                        return;
                    }

                    var worldX = x - SideLength - 1;
                    var worldY = (int)Heightmap[x, z];
                    var worldZ = z - SideLength - 1;

                    var treeHere = GetTreeAt(worldX, worldY, worldZ);
                    if (treeHere == TreeType.None)
                        continue;

                    TreeDecorator.BuildTree(vbi, new Vector3(worldX, worldY, worldZ), treeHere);
                }

                worker.ReportProgress((int)(x / (2 * SideLength + 2f) * 100));
            }

            e.Result = vbi;
        }

        private void DoBackgroundDecorateComplete(object sender, RunWorkerCompletedEventArgs e)
        {
            // Render done, reset statusbar
            _terrainLayerList.Invoke((MethodInvoker)delegate
           {
               _terrainLayerList.bCancelRender.Visible = false;
               _terrainLayerList.bCancelRender.Enabled = false;

               _terrainLayerList.pbRenderStatus.Visible = false;
               _terrainLayerList.pbRenderStatus.Value = 0;

               _terrainLayerList.lRenderStatus.Text = EmbeddedFiles.Status_Ready;
           });

            // If the decorate was manually cancelled, go no further
            if (_scriptWatcher.GetScriptId() == 0 || e.Cancelled)
                return;

            // Take the render result and upload it to the VBO
            var result = (VertexBufferInitializer)e.Result;
            _decorVbo.InitializeVbo(result);
            GC.Collect();
        }

        private TreeType GetTreeAt(int worldX, int worldY, int worldZ)
        {
            return ScriptedTerrainGenerator.GetTree(worldX, worldY, worldZ);
        }

        private void UpdateHandler(object sender, FrameEventArgs e)
        {
            if (_shouldDie)
                Exit();

            // Grab the new keyboard state
            _keyboard = Keyboard.GetState();

            // Request a render if there are pending changes
            if (_dirty)
            {
                _terrainLayerList.ReRenderNoiseImage();
                ReRender();
                _dirty = false;
            }

            // Compute input-based rotations
            var delta = e.Time;
            var amount = _keyboard[Key.LShift] || _keyboard[Key.RShift] ? 45 : 90;

            if (Focused)
            {
                if (_keyboard[Key.Left])
                    _yaw += amount * delta;
                if (_keyboard[Key.Right])
                    _yaw -= amount * delta;
                if (_keyboard[Key.Up])
                    _pitch += amount * delta;
                if (_keyboard[Key.Down])
                    _pitch -= amount * delta;
                if (_keyboard[Key.R])
                {
                    _yaw = 45;
                    _pitch = 160;
                    _translation = new Vector3(0, -25, 0);
                }
            }
        }

        public void ReDecorate()
        {
            // Make sure the render requirements are met
            if (_terrainLayerList == null || _terrainLayerList.cbPauseGen.Checked)
                return;

            // If there's an ongoing render, cancel it
            if (IsDecorating())
                CancelDecorate();

            _terrainLayerList.Invoke((MethodInvoker)delegate
           {
               // Enable the render statusbar
               _terrainLayerList.bCancelRender.Enabled = true;
               _terrainLayerList.bCancelRender.Visible = true;

               _terrainLayerList.pbRenderStatus.Visible = true;
           });

            // Fire up the render
            _backgroundDecorator.RunWorkerAsync();
        }

        private void RenderHandler(object sender, FrameEventArgs e)
        {
            // Reset the view
            GL.Clear(ClearBufferMask.ColorBufferBit |
                     ClearBufferMask.DepthBufferBit |
                     ClearBufferMask.StencilBufferBit);

            // Reload the projection matrix
            var aspectRatio = Width / (float)Height;
            var projection = Matrix4.CreatePerspectiveFieldOfView(MathHelper.PiOver4, aspectRatio, 1, 1024);
            GL.MatrixMode(MatrixMode.Projection);
            GL.LoadMatrix(ref projection);

            var lookat = Matrix4.LookAt(0, 128, 256, 0, 0, 0, 0, 1, 0);
            GL.MatrixMode(MatrixMode.Modelview);
            GL.LoadMatrix(ref lookat);

            // "Center" the terrain
            GL.Translate(_translation);

            // Zoom and scale the terrain
            var scale = new Vector3(4 * (1 / _zoom), -4 * (1 / _zoom), 4 * (1 / _zoom));
            GL.Scale(scale);
            GL.Rotate(_pitch, 1.0f, 0.0f, 0.0f);
            GL.Rotate(_yaw, 0.0f, 1.0f, 0.0f);

            // Wireframe mode if selected
            GL.PolygonMode(MaterialFace.FrontAndBack,
                _terrainLayerList.cbWireframe.Checked ? PolygonMode.Line : PolygonMode.Fill);

            // Reset the frag shader uniforms
            Uniforms.Clear();

            // Set up uniforms
            TintUniform.Value = _tintColorVector;
            Uniforms.Add(TintUniform);

            GL.Color3(Color.White);

            // Engage shader, render, disengage
            _shaderProgram.Use(Uniforms);
            _terrainVbo.Render();
            GL.UseProgram(0);

            _decorVbo.Render();

            // Render the ocean
            GL.Color3(Color.MediumBlue);

            var waterLevel = ScriptedTerrainGenerator.GetWaterLevel();

            GL.Begin(PrimitiveType.Quads);
            GL.Normal3(UpVector);
            GL.Vertex3(-SideLength, waterLevel - 0.1, -SideLength);
            GL.Vertex3(SideLength, waterLevel - 0.1, -SideLength);
            GL.Vertex3(SideLength, waterLevel - 0.1, SideLength);
            GL.Vertex3(-SideLength, waterLevel - 0.1, SideLength);
            GL.End();

            // Set up 2D mode
            GL.MatrixMode(MatrixMode.Projection);
            GL.LoadIdentity();
            GL.Ortho(0, Width, Height, 0, 1, -1);
            GL.MatrixMode(MatrixMode.Modelview);
            GL.LoadIdentity();

            GL.PushMatrix();

            GL.Enable(EnableCap.Blend);
            GL.Disable(EnableCap.Lighting);
            GL.Color3(Color.White);

            // Render diagnostic data
            GL.Enable(EnableCap.Texture2D);
            if (Focused && _keyboard[Key.D])
            {
                // Static diagnostic header
                GL.PushMatrix();
                _font.RenderString($"FPS: {(int)Math.Ceiling(RenderFrequency)}");
                GL.Translate(0, _font.Common.LineHeight, 0);
                _font.RenderString($"Render Time: {RenderTime * 1000}ms");
                GL.Translate(0, _font.Common.LineHeight, 0);
                _font.RenderString($"Verts: {_numVerts}");
                GL.PopMatrix();
            }
            else
            {
                // Info footer
                GL.PushMatrix();
                _font.RenderString($"{EmbeddedFiles.AppName} - Development Build");
                GL.Translate(0, Height - _font.Common.LineHeight, 0);
                _font.RenderString("Press 'D' for info");
                GL.PopMatrix();
            }

            GL.Disable(EnableCap.Texture2D);

            GL.Enable(EnableCap.Lighting);
            GL.Disable(EnableCap.Blend);

            GL.PopMatrix();

            // Swap the graphics buffer
            SwapBuffers();
        }

        public static void DrawBox(VertexBufferInitializer vbi, Vector3 position, int color = 0xFF0000)
        {
            position.X -= 0.5f;
            position.Y += 0.5f;
            position.Z -= 0.5f;
            for (var i = 5; i >= 0; i--)
            {
                vbi.AddVertex(position + _boxVerts[_boxFaces[i, 0]], _boxNormals[i], color);
                vbi.AddVertex(position + _boxVerts[_boxFaces[i, 1]], _boxNormals[i], color);
                vbi.AddVertex(position + _boxVerts[_boxFaces[i, 2]], _boxNormals[i], color);
                vbi.AddVertex(position + _boxVerts[_boxFaces[i, 3]], _boxNormals[i], color);
            }
        }

        public void SetVoxels(bool voxels)
        {
            _voxels = voxels;
            ReRender(true, false);
        }
    }
}