﻿using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using TerrainGen.Generator;
using TerrainGen.Job;
using TerrainGen.Shader;
using TerrainGen.Util;
using TerrainGenCore;

namespace TerrainGen
{
    public class RenderManager
    {
        private readonly ConcurrentQueue<IJob> _fgJobs;
        private readonly ConcurrentQueue<IJob> _bgJobs;
        private readonly CsTerrainGenerator _generator;
        private readonly EventWaitHandle _workerHandle;
        private readonly ShaderProgram _shaderProgram;
        private readonly Uniform _tintUniform = new Uniform("tint");

        private Thread _worker;

        public Chunk[] Chunks { get; private set; }
        public Vector3 TintColor { get; set; }
        public int SideLength { get; set; }

        public RenderManager(CsTerrainGenerator generator)
        {
            _fgJobs = new ConcurrentQueue<IJob>();
            _bgJobs = new ConcurrentQueue<IJob>();
            _generator = generator;
            _shaderProgram = new DefaultShaderProgram(EmbeddedFiles.default_fs);
            _shaderProgram.InitProgram();
            _workerHandle = new EventWaitHandle(false, EventResetMode.ManualReset);
            CreateChunks();
        }

        public void CreateChunks()
        {
            Chunks = new Chunk[SideLength * SideLength];
            for (var i = 0; i < SideLength; i++)
                for (var j = 0; j < SideLength; j++)
                {
                    Chunks[i * SideLength + j] = new Chunk(i, j);
                }
        }

        public void EnqueueJob(IJob job)
        {
            if (job.CanExecuteInBackground())
            {
                _bgJobs.Enqueue(job);
                _workerHandle.Set();
            }
            else
                _fgJobs.Enqueue(job);
        }

        public void UpdateJobs()
        {
            if (_worker is null)
            {
                _worker = new Thread(() =>
                {
                    while (true)
                    {
                        while (!_bgJobs.IsEmpty)
                        {
                            if (!_bgJobs.TryDequeue(out var job))
                            {
                                Lumberjack.Warn("Unable to dequeue BG job");
                                return;
                            }
                            job.Execute(this);
                        }
                        
                        _workerHandle.Reset();
                        _workerHandle.WaitOne();
                    }
                });
                _worker.Start();
            }

            while (!_fgJobs.IsEmpty)
            {
                if (!_fgJobs.TryDequeue(out var job))
                {
                    Lumberjack.Warn("Unable to dequeue FG job");
                    return;
                }
                job.Execute(this);
                Application.DoEvents();
            }
        }

        public void Render()
        {
            // Set up uniforms
            _tintUniform.Value = TintColor;

            GL.Color3(Color.White);

            GL.PushMatrix();
            GL.Translate(-SideLength * 8 + 1, 0, -SideLength * 8 + 1);
            // Engage shader, render, disengage
            _shaderProgram.Use(_tintUniform);
            foreach (var chunk in Chunks)
                chunk?.Draw();
            GL.UseProgram(0);
            GL.PopMatrix();

            // Render the ocean
            GL.Color3(Color.MediumBlue);

            var waterLevel = _generator.GetWaterLevel();

            GL.Begin(PrimitiveType.Quads);
            GL.Normal3(Vector3.UnitY);
            GL.Vertex3(-SideLength * 8, waterLevel - 0.4, -SideLength * 8);
            GL.Vertex3(SideLength * 8, waterLevel - 0.4, -SideLength * 8);
            GL.Vertex3(SideLength * 8, waterLevel - 0.4, SideLength * 8);
            GL.Vertex3(-SideLength * 8, waterLevel - 0.4, SideLength * 8);
            GL.End();
        }

        public void Rebuild()
        {
            CancelJobs();
            EnqueueJob(new JobRebuildChunks(_generator));
        }

        public void CancelJobs()
        {
            var fgJobs = new List<IJob>();
            var bgJobs = new List<IJob>();

            while (!_fgJobs.IsEmpty)
            {
                if (!_fgJobs.TryDequeue(out var j))
                    continue;
                fgJobs.Add(j);
            }

            while (!_bgJobs.IsEmpty)
            {
                if (!_bgJobs.TryDequeue(out var j))
                    continue;
                bgJobs.Add(j);
            }

            foreach (var job in fgJobs)
                _fgJobs.Enqueue(job);

            foreach (var job in bgJobs)
                _bgJobs.Enqueue(job);
        }

        public void SetSeed(long seed)
        {
            ProcNoise.SetSeed(seed);
        }

        public void Kill()
        {
            _worker.Abort();
        }
    }
}
