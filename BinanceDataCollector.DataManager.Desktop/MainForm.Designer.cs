namespace BinanceDataCollector.DataManager.Desktop;

partial class MainForm
{
    /// <summary>
    ///  Required designer variable.
    /// </summary>
    private System.ComponentModel.IContainer components = null;

    /// <summary>
    ///  Clean up any resources being used.
    /// </summary>
    /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    /// <summary>
    ///  Required method for Designer support - do not modify
    ///  the contents of this method with the code editor.
    /// </summary>
    private void InitializeComponent()
    {
        menuStrip1 = new MenuStrip();
        tesCSVintegrityToolStripMenuItem = new ToolStripMenuItem();
        tradesDataIntegrityToolStripMenuItem = new ToolStripMenuItem();
        cSVToolStripMenuItem = new ToolStripMenuItem();
        databaseToolStripMenuItem1 = new ToolStripMenuItem();
        ohToolStripMenuItem = new ToolStripMenuItem();
        cSVDataToolStripMenuItem = new ToolStripMenuItem();
        databaseToolStripMenuItem = new ToolStripMenuItem();
        cVDToolStripMenuItem = new ToolStripMenuItem();
        databToolStripMenuItem = new ToolStripMenuItem();
        symbolsToolStripMenuItem = new ToolStripMenuItem();
        showDbSymbolsToolStripMenuItem = new ToolStripMenuItem();
        menuStrip1.SuspendLayout();
        SuspendLayout();
        // 
        // menuStrip1
        // 
        menuStrip1.Items.AddRange(new ToolStripItem[] { tesCSVintegrityToolStripMenuItem, tradesDataIntegrityToolStripMenuItem, ohToolStripMenuItem, cVDToolStripMenuItem, symbolsToolStripMenuItem });
        menuStrip1.Location = new Point(0, 0);
        menuStrip1.Name = "menuStrip1";
        menuStrip1.Size = new Size(691, 24);
        menuStrip1.TabIndex = 2;
        menuStrip1.Text = "menuStrip1";
        // 
        // tesCSVintegrityToolStripMenuItem
        // 
        tesCSVintegrityToolStripMenuItem.Name = "tesCSVintegrityToolStripMenuItem";
        tesCSVintegrityToolStripMenuItem.Size = new Size(87, 20);
        tesCSVintegrityToolStripMenuItem.Text = "CSV integrity";
        // 
        // tradesDataIntegrityToolStripMenuItem
        // 
        tradesDataIntegrityToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { cSVToolStripMenuItem, databaseToolStripMenuItem1 });
        tradesDataIntegrityToolStripMenuItem.Name = "tradesDataIntegrityToolStripMenuItem";
        tradesDataIntegrityToolStripMenuItem.Size = new Size(100, 20);
        tradesDataIntegrityToolStripMenuItem.Text = "Trades integrity";
        // 
        // cSVToolStripMenuItem
        // 
        cSVToolStripMenuItem.Name = "cSVToolStripMenuItem";
        cSVToolStripMenuItem.Size = new Size(122, 22);
        cSVToolStripMenuItem.Text = "CSV";
        cSVToolStripMenuItem.Click += TradesCSVMenu_Click;
        // 
        // databaseToolStripMenuItem1
        // 
        databaseToolStripMenuItem1.Name = "databaseToolStripMenuItem1";
        databaseToolStripMenuItem1.Size = new Size(122, 22);
        databaseToolStripMenuItem1.Text = "Database";
        databaseToolStripMenuItem1.Click += TradesDatabaseMenu_Click;
        // 
        // ohToolStripMenuItem
        // 
        ohToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { cSVDataToolStripMenuItem, databaseToolStripMenuItem });
        ohToolStripMenuItem.Name = "ohToolStripMenuItem";
        ohToolStripMenuItem.Size = new Size(97, 20);
        ohToolStripMenuItem.Text = "Ohlcv integrity";
        // 
        // cSVDataToolStripMenuItem
        // 
        cSVDataToolStripMenuItem.Name = "cSVDataToolStripMenuItem";
        cSVDataToolStripMenuItem.Size = new Size(180, 22);
        cSVDataToolStripMenuItem.Text = "CSV data";
        cSVDataToolStripMenuItem.Click += OhlcvCsv_Click;
        // 
        // databaseToolStripMenuItem
        // 
        databaseToolStripMenuItem.Name = "databaseToolStripMenuItem";
        databaseToolStripMenuItem.Size = new Size(180, 22);
        databaseToolStripMenuItem.Text = "Database";
        databaseToolStripMenuItem.Click += OhlcvDatabase_Click;
        // 
        // cVDToolStripMenuItem
        // 
        cVDToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { databToolStripMenuItem });
        cVDToolStripMenuItem.Name = "cVDToolStripMenuItem";
        cVDToolStripMenuItem.Size = new Size(40, 20);
        cVDToolStripMenuItem.Text = "CSV";
        // 
        // databToolStripMenuItem
        // 
        databToolStripMenuItem.Name = "databToolStripMenuItem";
        databToolStripMenuItem.Size = new Size(122, 22);
        databToolStripMenuItem.Text = "Database";
        // 
        // symbolsToolStripMenuItem
        // 
        symbolsToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { showDbSymbolsToolStripMenuItem });
        symbolsToolStripMenuItem.Name = "symbolsToolStripMenuItem";
        symbolsToolStripMenuItem.Size = new Size(64, 20);
        symbolsToolStripMenuItem.Text = "Symbols";
        // 
        // showDbSymbolsToolStripMenuItem
        // 
        showDbSymbolsToolStripMenuItem.Name = "showDbSymbolsToolStripMenuItem";
        showDbSymbolsToolStripMenuItem.Size = new Size(168, 22);
        showDbSymbolsToolStripMenuItem.Text = "Show db Symbols";
        // 
        // MainForm
        // 
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(691, 199);
        Controls.Add(menuStrip1);
        MainMenuStrip = menuStrip1;
        Name = "MainForm";
        StartPosition = FormStartPosition.CenterScreen;
        Text = "MainForm";
        menuStrip1.ResumeLayout(false);
        menuStrip1.PerformLayout();
        ResumeLayout(false);
        PerformLayout();
    }

    #endregion
    private MenuStrip menuStrip1;
    private ToolStripMenuItem tesCSVintegrityToolStripMenuItem;
    private ToolStripMenuItem tradesDataIntegrityToolStripMenuItem;
    private ToolStripMenuItem cSVToolStripMenuItem;
    private ToolStripMenuItem databaseToolStripMenuItem1;
    private ToolStripMenuItem ohToolStripMenuItem;
    private ToolStripMenuItem cSVDataToolStripMenuItem;
    private ToolStripMenuItem databaseToolStripMenuItem;
    private ToolStripMenuItem cVDToolStripMenuItem;
    private ToolStripMenuItem databToolStripMenuItem;
    private ToolStripMenuItem symbolsToolStripMenuItem;
    private ToolStripMenuItem showDbSymbolsToolStripMenuItem;
}
