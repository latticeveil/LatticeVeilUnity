using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace LatticeVeil.Installer
{
    /// <summary>
    /// Minimal installer wizard: pick the install root (persisted as
    /// LATTICEVEIL_INSTALL_ROOT for the launcher/game), optionally install the
    /// launcher build, and manage/uninstall installed game versions.
    /// </summary>
    public sealed class MainForm : Form
    {
        private readonly InstallService _service;

        private readonly TextBox _rootBox = new TextBox { Dock = DockStyle.Top, Width = 420 };
        private readonly Button _applyButton = new Button { Text = "Set install location", Dock = DockStyle.Top, Height = 32 };
        private readonly Button _browseRootButton = new Button { Text = "Browse...", Dock = DockStyle.Top, Height = 28 };
        private readonly Button _launcherButton = new Button { Text = "Install launcher build...", Dock = DockStyle.Top, Height = 32 };
        private readonly ListView _versionList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
        private readonly Button _removeButton = new Button { Text = "Uninstall selected version", Dock = DockStyle.Bottom, Height = 32 };
        private readonly Label _status = new Label { Dock = DockStyle.Bottom, Height = 24, TextAlign = ContentAlignment.MiddleLeft };

        public MainForm(InstallService service)
        {
            _service = service;

            Text = "LatticeVeil Installer";
            Size = new Size(640, 560);
            StartPosition = FormStartPosition.CenterScreen;

            var rootGroup = new GroupBox { Text = "Install location", Dock = DockStyle.Top, Height = 140 };
            _rootBox.Text = _service.RootDir;
            rootGroup.Controls.Add(_applyButton);
            rootGroup.Controls.Add(_browseRootButton);
            rootGroup.Controls.Add(_rootBox);
            _applyButton.Top = 36;
            _browseRootButton.Top = 72;
            _rootBox.Top = 8;
            _rootBox.Width = 560;

            var versionsGroup = new GroupBox { Text = "Installed game versions", Dock = DockStyle.Fill };
            versionsGroup.Controls.Add(_versionList);
            versionsGroup.Controls.Add(_removeButton);

            _versionList.Columns.Add("Version", 180);
            _versionList.Columns.Add("Size", 100);
            _versionList.Columns.Add("Executable", 320);

            Controls.Add(versionsGroup);
            Controls.Add(rootGroup);
            Controls.Add(_launcherButton);
            Controls.Add(_status);

            _applyButton.Click += (s, e) =>
            {
                try
                {
                    _service.SetRoot(_rootBox.Text.Trim());
                    _service.Apply();
                    _status.Text = $"Install location set: {_service.RootDir} (LATTICEVEIL_INSTALL_ROOT)";
                    RefreshVersions();
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Error"); }
            };

            _browseRootButton.Click += (s, e) =>
            {
                using var dialog = new FolderBrowserDialog { SelectedPath = _rootBox.Text };
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    _rootBox.Text = dialog.SelectedPath;
            };

            _launcherButton.Click += (s, e) =>
            {
                using var dialog = new FolderBrowserDialog { Description = "Select the launcher build folder (contains the launcher .exe)" };
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var dest = _service.InstallLauncher(dialog.SelectedPath);
                    _status.Text = $"Launcher installed: {dest}";
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Error"); }
            };

            _removeButton.Click += (s, e) =>
            {
                if (_versionList.SelectedItems.Count == 0) return;
                var tag = _versionList.SelectedItems[0].Text;
                if (MessageBox.Show(this, $"Uninstall version '{tag}'?", "Confirm",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                _service.UninstallVersion(tag);
                RefreshVersions();
                _status.Text = $"Uninstalled: {tag}";
            };

            RefreshVersions();
        }

        private void RefreshVersions()
        {
            _versionList.Items.Clear();
            foreach (var v in _service.GetInstalledVersions())
            {
                var item = new ListViewItem(v.Tag);
                item.SubItems.Add($"{v.SizeBytes / 1_000_000.0:0} MB");
                item.SubItems.Add(v.ExePath);
                _versionList.Items.Add(item);
            }
        }
    }
}
