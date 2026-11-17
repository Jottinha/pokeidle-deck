namespace PokeIdleMulti;

/// <summary>Cores do deck (tema escuro) e o desenho das barras.</summary>
internal static class Theme
{
    // Fundo bem escuro entre as contas, barras um degrau acima e o cabeçalho das contas mais um: cada camada se
    // separa da outra sem precisar de linhas. Texto quase branco e o cinza das legendas claro o bastante para ler.
    public static readonly Color Back = Color.FromArgb(11, 12, 17);
    public static readonly Color Bar = Color.FromArgb(24, 26, 35);
    public static readonly Color Header = Color.FromArgb(31, 34, 46);
    public static readonly Color Hover = Color.FromArgb(52, 57, 76);
    public static readonly Color Border = Color.FromArgb(52, 56, 74);
    public static readonly Color Accent = Color.FromArgb(132, 112, 255);
    public static readonly Color AccentDim = Color.FromArgb(64, 54, 128);
    public static readonly Color Text = Color.FromArgb(242, 243, 248);
    public static readonly Color Muted = Color.FromArgb(172, 177, 197);
    public static readonly Color Ok = Color.FromArgb(52, 211, 120);
    public static readonly Color Warn = Color.FromArgb(250, 204, 21);
    public static readonly Color Bad = Color.FromArgb(244, 94, 80);
    public static readonly Color Vote = Color.FromArgb(255, 160, 40);

    /// <summary>A fonte das barras: um pouco maior que o padrão de 9 pt, para ler de longe.</summary>
    public static readonly Font BarFont = new("Segoe UI", 10f);

    /// <summary>
    /// Botão da barra desenhado como "pílula" preenchida: <see cref="Pill.Active"/> o layout em uso (roxo),
    /// <see cref="Pill.On"/> uma opção ligada (verde, como o Eco e o Ultra). Vai no Tag do item.
    /// </summary>
    public enum Pill { Active, On }

    private static readonly Color OnFill = Color.FromArgb(22, 120, 74);

    /// <summary>Marca (ou desmarca) um item como pílula.</summary>
    public static void SetPill(ToolStripItem item, Pill? pill)
    {
        if (Equals(item.Tag, pill))
            return;
        item.Tag = pill;
        item.Invalidate();
    }

    public static ToolStripRenderer Renderer { get; } = new DarkRenderer();

    /// <summary>
    /// Os ícones do próprio Windows (Segoe MDL2 Assets). Para o que a fonte normal não tem: o ⏻, por exemplo,
    /// vira um retângulo vazio no Windows 10.
    /// </summary>
    public static readonly Font Symbols = new("Segoe MDL2 Assets", 11);

    /// <summary>O ⏻ dos ícones do Windows (usar com <see cref="Symbols"/>).</summary>
    public const string PowerGlyph = "";

    /// <summary>Ampliar, recarregar e o som dos ícones do Windows: do mesmo tamanho do ⏻ (os da fonte normal saem miúdos).</summary>
    public const string FocusGlyph = "";
    public const string ReloadGlyph = "";
    public const string SoundGlyph = "";
    public const string MutedGlyph = "";

    /// <summary>Os três pontos (⋯) dos ícones do Windows: o menu da conta.</summary>
    public const string MoreGlyph = "";

    /// <summary>O carrinho dos ícones do Windows (a recompra).</summary>
    public const string CartGlyph = "";

    /// <summary>O ícone do deck (o mesmo do exe), na barra de título e na barra de tarefas.</summary>
    public static Icon AppIcon { get; } = new(typeof(Theme).Assembly.GetManifestResourceStream("deck.ico")!);

    /// <summary>
    /// Texto de dica com limite: quebra cada linha nos espaços em até <paramref name="width"/> letras (a dica da barra
    /// não quebra sozinha e cresce pela tela inteira) e corta em <paramref name="maxLines"/> linhas, com "…".
    /// </summary>
    public static string Wrap(string text, int width = 60, int maxLines = 12)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = new System.Text.StringBuilder();
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length > 0 && line.Length + 1 + word.Length > width)
                {
                    lines.Add(line.ToString());
                    line.Clear();
                }
                line.Append(line.Length > 0 ? " " : "").Append(word);
            }
            lines.Add(line.ToString());
        }
        if (lines.Count > maxLines)
            lines = lines.Take(maxLines - 1).Append("…").ToList();
        return string.Join("\n", lines);
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Barra de título escura do Windows 10/11 (chamar depois que a janela tem handle).</summary>
    public static void DarkTitleBar(Form form)
    {
        int on = 1;
        // 20 nas versões atuais; 19 nas primeiras do Windows 10 que tinham o modo escuro.
        if (DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(form.Handle, 19, ref on, sizeof(int));
    }

    public static void Style(ToolStrip strip)
    {
        strip.RenderMode = ToolStripRenderMode.ManagerRenderMode;
        strip.GripStyle = ToolStripGripStyle.Hidden;
        strip.BackColor = Bar;
        strip.ForeColor = Text;
    }

    private static System.Drawing.Drawing2D.GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) => RoundedEdges = false;

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            // Itens sem cor própria (inclusive os dos menus) usam o texto claro do tema; a pílula, branco.
            e.TextColor = !e.Item.Enabled ? Muted
                : e.Item.Tag is Pill ? Color.White
                : e.Item.ForeColor == SystemColors.ControlText ? Text
                : e.Item.ForeColor;
            base.OnRenderItemText(e);
        }

        /// <summary>A pílula: fundo arredondado preenchido, um pouco mais claro com o mouse em cima.</summary>
        private static bool DrawPill(Graphics g, ToolStripItem item)
        {
            if (item.Tag is not Pill pill)
                return false;
            var fill = pill == Pill.Active ? Accent : OnFill;
            if (item.Selected || item.Pressed)
                fill = ControlPaint.Light(fill, 0.2f);
            var r = new Rectangle(1, 2, item.Width - 3, item.Height - 5);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = Rounded(r, 6);
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
            return true;
        }

        protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
        {
            if (!DrawPill(e.Graphics, e.Item))
                base.OnRenderButtonBackground(e);
        }

        protected override void OnRenderDropDownButtonBackground(ToolStripItemRenderEventArgs e)
        {
            if (!DrawPill(e.Graphics, e.Item))
                base.OnRenderDropDownButtonBackground(e);
        }

        protected override void OnRenderSplitButtonBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item is not ToolStripSplitButton split || !DrawPill(e.Graphics, e.Item))
            {
                base.OnRenderSplitButtonBackground(e);
                return;
            }
            // A setinha por cima da pílula, separada do botão por uma linha clara.
            var arrow = split.DropDownButtonBounds;
            using (var pen = new Pen(Color.FromArgb(90, 255, 255, 255)))
                e.Graphics.DrawLine(pen, arrow.Left, arrow.Top + 6, arrow.Left, arrow.Bottom - 7);
            DrawArrow(new ToolStripArrowRenderEventArgs(e.Graphics, split, arrow, Color.White, ArrowDirection.Down));
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item?.Tag is Pill ? Color.White : Text;
            base.OnRenderArrow(e);
        }
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripGradientBegin => Bar;
        public override Color ToolStripGradientMiddle => Bar;
        public override Color ToolStripGradientEnd => Bar;
        public override Color ToolStripBorder => Bar;
        public override Color ToolStripDropDownBackground => Bar;
        public override Color StatusStripGradientBegin => Bar;
        public override Color StatusStripGradientEnd => Bar;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color ImageMarginGradientBegin => Bar;
        public override Color ImageMarginGradientMiddle => Bar;
        public override Color ImageMarginGradientEnd => Bar;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Bar;
        public override Color ButtonSelectedBorder => Hover;
        public override Color ButtonSelectedHighlight => Hover;
        public override Color ButtonSelectedGradientBegin => Hover;
        public override Color ButtonSelectedGradientMiddle => Hover;
        public override Color ButtonSelectedGradientEnd => Hover;
        public override Color ButtonPressedBorder => Accent;
        public override Color ButtonPressedHighlight => AccentDim;
        public override Color ButtonPressedGradientBegin => AccentDim;
        public override Color ButtonPressedGradientMiddle => AccentDim;
        public override Color ButtonPressedGradientEnd => AccentDim;
        public override Color ButtonCheckedHighlight => AccentDim;
        public override Color ButtonCheckedHighlightBorder => Accent;
        public override Color ButtonCheckedGradientBegin => AccentDim;
        public override Color ButtonCheckedGradientMiddle => AccentDim;
        public override Color ButtonCheckedGradientEnd => AccentDim;
        public override Color CheckBackground => AccentDim;
        public override Color CheckSelectedBackground => Accent;
        public override Color CheckPressedBackground => Accent;
    }
}
