using System;
using Silk.NET.OpenGL;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>
/// <see cref="IProgramBuildOps"/> over a real GL context with
/// KHR_parallel_shader_compile. Compile and link are submitted without a status
/// read; completion is polled through GL_COMPLETION_STATUS_KHR, which never
/// forces the driver to finish. Link is only submitted once both shaders report
/// complete, so the link call itself cannot wait on a compile.
/// </summary>
internal sealed class GlProgramBuildOps : IProgramBuildOps
{
    internal const uint CompletionStatusKhr = 0x91B1;

    private readonly GL _gl;
    private readonly string _vertexSource;

    public GlProgramBuildOps(GL gl, string vertexSource)
    {
        _gl = gl;
        _vertexSource = vertexSource;
    }

    public PendingShaders StartCompile(string fragSource)
    {
        var vs = _gl.CreateShader(ShaderType.VertexShader);
        _gl.ShaderSource(vs, _vertexSource);
        _gl.CompileShader(vs);
        var fs = _gl.CreateShader(ShaderType.FragmentShader);
        _gl.ShaderSource(fs, fragSource);
        _gl.CompileShader(fs);
        return new PendingShaders(vs, fs);
    }

    public bool CompileComplete(PendingShaders s) => ShaderDone(s.Vertex) && ShaderDone(s.Fragment);

    private bool ShaderDone(uint shader)
    {
        _gl.GetShader(shader, (GLEnum)CompletionStatusKhr, out int done);
        return done != 0;
    }

    public string? CompileResult(PendingShaders s)
    {
        _gl.GetShader(s.Vertex, ShaderParameterName.CompileStatus, out int vsOk);
        if (vsOk == 0)
        {
            var log = "VertexShader compile failed: " + _gl.GetShaderInfoLog(s.Vertex);
            DeleteShaders(s);
            return log;
        }
        _gl.GetShader(s.Fragment, ShaderParameterName.CompileStatus, out int fsOk);
        if (fsOk == 0)
        {
            var log = _gl.GetShaderInfoLog(s.Fragment);
            DeleteShaders(s);
            return string.IsNullOrWhiteSpace(log) ? "shader failed to compile" : log;
        }
        return null;
    }

    public uint StartLink(PendingShaders s)
    {
        var prog = _gl.CreateProgram();
        _gl.AttachShader(prog, s.Vertex);
        _gl.AttachShader(prog, s.Fragment);
        _gl.LinkProgram(prog);
        return prog;
    }

    public bool LinkComplete(uint program)
    {
        _gl.GetProgram(program, (GLEnum)CompletionStatusKhr, out int done);
        return done != 0;
    }

    public string? LinkResult(uint program, PendingShaders s)
    {
        _gl.GetProgram(program, GLEnum.LinkStatus, out int ok);
        if (ok == 0)
        {
            var log = _gl.GetProgramInfoLog(program);
            _gl.DeleteProgram(program);
            DeleteShaders(s);
            return string.IsNullOrWhiteSpace(log) ? "no info log" : log;
        }
        _gl.DetachShader(program, s.Vertex);
        _gl.DetachShader(program, s.Fragment);
        DeleteShaders(s);
        return null;
    }

    public void Discard(PendingShaders s, uint program)
    {
        if (program != 0)
        {
            _gl.DeleteProgram(program);
        }
        DeleteShaders(s);
    }

    private void DeleteShaders(PendingShaders s)
    {
        _gl.DeleteShader(s.Vertex);
        _gl.DeleteShader(s.Fragment);
    }
}
