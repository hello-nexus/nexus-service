using Nexus.Service.Lighting.Engine.Gpu;
using Xunit;

namespace Nexus.Service.Tests.Lighting.Gpu;

public class GlslDialectTests
{
    [Fact]
    public void Version_line_becomes_es300_with_default_precision_and_no_extra_line()
    {
        var src = "#version 330 core\nout vec4 c;\nvoid main() { c = vec4(1.0); }\n";

        var es = GlslDialect.ToEs300(src);

        Assert.StartsWith("#version 300 es\nprecision highp float; precision highp int; out vec4 c;\n", es);
        Assert.Equal(src.Split('\n').Length, es.Split('\n').Length);
        Assert.EndsWith("void main() { c = vec4(1.0); }\n", es);
    }

    [Fact]
    public void Directive_on_the_next_line_gets_precision_on_its_own_line()
    {
        var es = GlslDialect.ToEs300("#version 330 core\n#define K 1.0\nvoid main() {}\n");

        Assert.Equal("#version 300 es\nprecision highp float; precision highp int; \n#define K 1.0\nvoid main() {}\n", es);
    }

    [Theory]
    [InlineData("#version 330 core\r\nvoid main() {}")]
    [InlineData("\n#version 330 core\nvoid main() {}")]
    [InlineData("#version 330\nvoid main() {}")]
    public void Variants_of_the_330_line_are_rewritten(string src)
    {
        var es = GlslDialect.ToEs300(src);

        Assert.Contains("#version 300 es", es);
        Assert.DoesNotContain("330", es);
        Assert.Contains("precision highp float;", es);
    }

    [Theory]
    [InlineData("#version 410 core\nvoid main() {}")]
    [InlineData("#version 300 es\nvoid main() {}")]
    [InlineData("not glsl")]
    [InlineData("")]
    public void Other_text_passes_through_for_the_driver_to_judge(string src)
    {
        Assert.Equal(src, GlslDialect.ToEs300(src));
    }

    [Fact]
    public void Desktop_contexts_are_untouched()
    {
        var src = "#version 330 core\nvoid main() {}";

        Assert.Equal(src, GlslDialect.For(false, src));
        Assert.NotEqual(src, GlslDialect.For(true, src));
    }

    [Fact]
    public void Translation_keeps_the_rest_of_the_source_byte_for_byte()
    {
        var body = "uniform float u_time;\nvoid main() { float a = 0.5 * u_time; }\n";

        Assert.EndsWith(body, GlslDialect.ToEs300("#version 330 core\n" + body));
    }

    [Theory]
    [InlineData("float k = 1;", "float k = 1.0;")]
    [InlineData("float a = pow(x, 2);", "float a = pow(x, 2.0);")]
    [InlineData("float a = max(v, 0);", "float a = max(v, 0.0);")]
    [InlineData("vec3 c = vec3(1, 0, 0);", "vec3 c = vec3(1.0, 0.0, 0.0);")]
    [InlineData("float h = x * 2 + 3.5e2;", "float h = x * 2.0 + 3.5e2;")]
    public void Integer_literals_in_float_contexts_become_floats(string line, string expected)
    {
        Assert.Equal(expected, GlslDialect.FixIntLiteralsForEs(line));
    }

    [Theory]
    [InlineData("for (int i = 0; i < 4; i++) {")]
    [InlineData("int n = 3;")]
    [InlineData("float a = u_arr[2];")]
    [InlineData("int m = k % 2;")]
    [InlineData("#define COUNT 4")]
    [InlineData("uint h = 3u;")]
    [InlineData("float a = 0x10 + 1e3 + 2.0;")]
    [InlineData("float a = x; // pow(x, 2)")]
    public void Integer_contexts_and_non_decimal_numbers_are_left_alone(string line)
    {
        Assert.Equal(line, GlslDialect.FixIntLiteralsForEs(line));
    }

    [Theory]
    [InlineData("float d = texture2D(t, uv).r;")]
    [InlineData("uniform sampler1D t;")]
    [InlineData("gl_FragColor = vec4(1.0);")]
    [InlineData("double x = 1.0;")]
    [InlineData("float v = fma(a, b, c);")]
    [InlineData("#extension GL_ARB_foo : enable")]
    public void Desktop_only_constructs_are_refused(string line)
    {
        Assert.NotNull(GlslDialect.EsIncompatibility("#version 330 core\n" + line + "\n"));
    }

    [Theory]
    [InlineData("float v = fract(sin(x) * 43758.5);")]
    [InlineData("// texture2D was removed\nfloat v = 1.0;")]
    [InlineData("float doubled = 2.0; float fmax_ = 1.0;")]
    public void Portable_sources_are_accepted(string body)
    {
        Assert.Null(GlslDialect.EsIncompatibility("#version 330 core\n" + body + "\n"));
    }

    [Theory]
    [InlineData("float e = 1e-3;")]
    [InlineData("float e = 2.5E+4 * 1.0e-2;")]
    public void Exponents_are_not_rewritten(string line)
    {
        Assert.Equal(line, GlslDialect.FixIntLiteralsForEs(line));
    }

    [Fact]
    public void Literal_after_a_minus_that_follows_an_identifier_is_still_widened()
    {
        Assert.Equal("float a = size - 2.0;", GlslDialect.FixIntLiteralsForEs("float a = size - 2;"));
    }

    [Theory]
    [InlineData("/* texture2D(t, uv) and gl_FragColor */ float v = 1.0;")]
    [InlineData("/* multi\nline fma(a, b, c)\n*/ float v = 1.0;")]
    [InlineData("float fma(float a, float b, float c) { return a * b + c; }\nfloat v = fma(1.0, 2.0, 3.0);")]
    [InlineData("vec3 fma(vec3 a, vec3 b, vec3 c) { return a * b + c; }")]
    public void Comments_and_a_user_defined_fma_are_accepted(string body)
    {
        Assert.Null(GlslDialect.EsIncompatibility("#version 330 core\n" + body + "\n"));
    }

    [Fact]
    public void Calling_the_builtin_fma_is_refused()
    {
        Assert.NotNull(GlslDialect.EsIncompatibility("#version 330 core\nfloat v = fma(1.0, 2.0, 3.0);\n"));
    }
}
