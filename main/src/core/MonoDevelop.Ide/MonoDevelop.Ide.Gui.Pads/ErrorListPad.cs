// ErrorListPad.cs
//  
// Author:
//       Todd Berman <tberman@sevenl.net>
//       David Makovský <yakeen@sannyas-on.net>
//       Lluis Sanchez Gual <lluis@novell.com>
// 
// Copyright (c) 2004 Todd Berman
// Copyright (c) 2006 David Makovský
// Copyright (c) 2009 Novell, Inc (http://www.novell.com)
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.


using System;
using System.Drawing;
using System.CodeDom.Compiler;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;

using MonoDevelop.Core;
using MonoDevelop.Projects;
using MonoDevelop.Ide.Gui;
using MonoDevelop.Ide.Tasks;
using MonoDevelop.Ide.Gui.Content;

using Gtk;
using System.Text;
using MonoDevelop.Components.Docking;
using MonoDevelop.Ide.Gui.Components;
using MonoDevelop.Components.Commands;
using MonoDevelop.Ide.Commands;
using MonoDevelop.Components;
using System.Linq;
using MonoDevelop.Components.AutoTest;
using System.ComponentModel;

namespace MonoDevelop.Ide.Gui.Pads
{
	class ErrorListPad : PadContent
	{
		HPaned control;
		ScrolledWindow sw;
		PadTreeView view;
		LogView outputView;
		TreeStore store;
		TreeModelFilter filter;
		TreeModelSort sort;
		ToggleButton errorBtn, warnBtn, msgBtn, logBtn;
		SearchEntry searchEntry;
		string currentSearchPattern = null;
		Hashtable tasks = new Hashtable ();
		int errorCount;
		int warningCount;
		int infoCount;
		bool initialLogShow = true;

		Menu menu;
		Dictionary<ToggleAction, int> columnsActions = new Dictionary<ToggleAction, int> ();
		Clipboard clipboard;

		Xwt.Drawing.Image iconWarning;
		Xwt.Drawing.Image iconError;
		Xwt.Drawing.Image iconInfo;
		Xwt.Drawing.Image iconEmpty;

		public readonly ConfigurationProperty<bool> ShowErrors = ConfigurationProperty.Create ("SharpDevelop.TaskList.ShowErrors", true);
		public readonly ConfigurationProperty<bool> ShowWarnings = ConfigurationProperty.Create ("SharpDevelop.TaskList.ShowWarnings", true);
		public readonly ConfigurationProperty<bool> ShowMessages = ConfigurationProperty.Create ("SharpDevelop.TaskList.ShowMessages", true);
		public readonly ConfigurationProperty<double> LogSeparatorPosition = ConfigurationProperty.Create ("SharpDevelop.TaskList.LogSeparatorPosition", 0.5d);
		public readonly ConfigurationProperty<bool> OutputViewVisible = ConfigurationProperty.Create ("SharpDevelop.TaskList.OutputViewVisible", false);

		static class DataColumns
		{
			internal const int Type = 0;
			internal const int Read = 1;
			internal const int Task = 2;
			internal const int Description = 3;
		}
		
		static class VisibleColumns
		{
			internal const int Type        = 0;
			internal const int Marked      = 1;
			internal const int Line        = 2;
			internal const int Description = 3;
			internal const int File        = 4;
			internal const int Project     = 5;
			internal const int Path        = 6;
			internal const int Category    = 7;
		}

		public override Control Control {
			get {
				if (control == null)
					CreateControl ();
				return control;
			}
		}

		public override string Id {
			get { return "MonoDevelop.Ide.Gui.Pads.ErrorListPad"; }
		}

		protected override void Initialize (IPadWindow window)
		{
			window.Title = GettextCatalog.GetString ("Errors");

			DockItemToolbar toolbar = window.GetToolbar (DockPositionType.Top);
			
			errorBtn = new ToggleButton { Name = "toggleErrors" };
			errorBtn.Active = ShowErrors;
			errorBtn.Image = new ImageView (Stock.Error, Gtk.IconSize.Menu);
			errorBtn.Image.Show ();
			errorBtn.Toggled += new EventHandler (FilterChanged);
			errorBtn.TooltipText = GettextCatalog.GetString ("Show Errors");
			UpdateErrorsNum();
			toolbar.Add (errorBtn);
			
			warnBtn = new ToggleButton  { Name = "toggleWarnings" };
			warnBtn.Active = ShowWarnings;
			warnBtn.Image = new ImageView (Stock.Warning, Gtk.IconSize.Menu);
			warnBtn.Image.Show ();
			warnBtn.Toggled += new EventHandler (FilterChanged);
			warnBtn.TooltipText = GettextCatalog.GetString ("Show Warnings");
			UpdateWarningsNum();
			toolbar.Add (warnBtn);

			msgBtn = new ToggleButton  { Name = "toggleMessages" };
			msgBtn.Active = ShowMessages;
			msgBtn.Image = new ImageView (Stock.Information, Gtk.IconSize.Menu);
			msgBtn.Image.Show ();
			msgBtn.Toggled += new EventHandler (FilterChanged);
			msgBtn.TooltipText = GettextCatalog.GetString ("Show Messages");
			UpdateMessagesNum();
			toolbar.Add (msgBtn);
			
			toolbar.Add (new SeparatorToolItem ());
			
			logBtn = new ToggleButton { Name = "toggleBuildOutput" };
			logBtn.Label = GettextCatalog.GetString ("Build Output");
			logBtn.Image = new ImageView ("md-message-log", Gtk.IconSize.Menu);
			logBtn.Image.Show ();
			logBtn.TooltipText = GettextCatalog.GetString ("Show build output");
			logBtn.Toggled += HandleLogBtnToggled;
			toolbar.Add (logBtn);

			//Dummy widget to take all space between "Build Output" button and SearchEntry
			toolbar.Add (new HBox (), true);

			searchEntry = new SearchEntry ();
			searchEntry.Entry.Changed += searchPatternChanged;
			searchEntry.WidthRequest = 200;
			searchEntry.Visible = true;
			toolbar.Add (searchEntry);

			toolbar.ShowAll ();

			UpdatePadIcon ();
		}

		void searchPatternChanged (object sender, EventArgs e)
		{
			currentSearchPattern = searchEntry.Entry.Text;
			filter.Refilter ();
		}

		void CreateControl ()
		{
			control = new HPaned ();

			store = new Gtk.TreeStore (typeof (Xwt.Drawing.Image), // image - type
									   typeof (bool),       // read?
									   typeof (TaskListEntry),       // read? -- use Pango weight
									   typeof (string));
			SemanticModelAttribute modelAttr = new SemanticModelAttribute ("store__Type", "store__Read", "store__Task", "store__Description");
			TypeDescriptor.AddAttributes (store, modelAttr);

			TreeModelFilterVisibleFunc filterFunct = new TreeModelFilterVisibleFunc (FilterTasks);
			filter = new TreeModelFilter (store, null);
			filter.VisibleFunc = filterFunct;
			
			sort = new TreeModelSort (filter);
			sort.SetSortFunc (VisibleColumns.Type, SeverityIterSort);
			sort.SetSortFunc (VisibleColumns.Project, ProjectIterSort);
			sort.SetSortFunc (VisibleColumns.File, FileIterSort);
			
			view = new PadTreeView (sort);
			view.ShowExpanders = true;
			view.RulesHint = true;
			view.DoPopupMenu = (evnt) => IdeApp.CommandService.ShowContextMenu (view, evnt, CreateMenu ());
			AddColumns ();
			LoadColumnsVisibility ();
			view.Columns[VisibleColumns.Type].SortColumnId = VisibleColumns.Type;
			view.Columns[VisibleColumns.Project].SortColumnId = VisibleColumns.Project;
			view.Columns[VisibleColumns.File].SortColumnId = VisibleColumns.File;
			
			sw = new MonoDevelop.Components.CompactScrolledWindow ();
			sw.ShadowType = ShadowType.None;
			sw.Add (view);
			TaskService.Errors.TasksRemoved      += ShowResults;
			TaskService.Errors.TasksAdded        += TaskAdded;
			TaskService.Errors.TasksChanged      += TaskChanged;
			TaskService.Errors.CurrentLocationTaskChanged += HandleTaskServiceErrorsCurrentLocationTaskChanged;
			
			IdeApp.Workspace.FirstWorkspaceItemOpened += OnCombineOpen;
			IdeApp.Workspace.LastWorkspaceItemClosed += OnCombineClosed;
			
			view.RowActivated += new RowActivatedHandler (OnRowActivated);
			
			iconWarning = ImageService.GetIcon (Ide.Gui.Stock.Warning, Gtk.IconSize.Menu);
			iconError = ImageService.GetIcon (Ide.Gui.Stock.Error, Gtk.IconSize.Menu);
			iconInfo = ImageService.GetIcon (Ide.Gui.Stock.Information, Gtk.IconSize.Menu);
			iconEmpty = ImageService.GetIcon (Ide.Gui.Stock.Empty, Gtk.IconSize.Menu);
			
			control.Add1 (sw);
			
			outputView = new LogView { Name = "buildOutput" };
			control.Add2 (outputView);
			
			control.ShowAll ();
			
			control.SizeAllocated += HandleControlSizeAllocated;
			
			bool outputVisible = OutputViewVisible;
			if (outputVisible) {
				outputView.Visible = true;
				logBtn.Active = true;
			} else {
				outputView.Hide ();
			}
			
			sw.SizeAllocated += HandleSwSizeAllocated;
			
			// Load existing tasks
			foreach (TaskListEntry t in TaskService.Errors) {
				AddTask (t);
			}

			control.FocusChain = new Gtk.Widget [] { sw };
		}
		
		void HandleSwSizeAllocated (object o, SizeAllocatedArgs args)
		{
			if (!initialLogShow && outputView.Visible) {
				var val = (double) ((double) control.Position / (double) control.Allocation.Width);
				LogSeparatorPosition.Value = val;
			}
		}
		
		[GLib.ConnectBefore]
		void HandleControlSizeAllocated (object o, SizeAllocatedArgs args)
		{
			if (initialLogShow && outputView.Visible) {
				SetInitialOutputViewSize (args.Allocation.Width);
				initialLogShow = false;
			}
		}
		
		public ProgressMonitor GetBuildProgressMonitor ()
		{
			if (control == null)
				CreateControl ();
			return outputView.GetProgressMonitor ();
		}

		void HandleTaskServiceErrorsCurrentLocationTaskChanged (object sender, EventArgs e)
		{
			if (TaskService.Errors.CurrentLocationTask == null) {
				view.Selection.UnselectAll ();
				return;
			}
			TreeIter it;
			if (!view.Model.GetIterFirst (out it))
				return;
			do {
				TaskListEntry t = (TaskListEntry) view.Model.GetValue (it, DataColumns.Task);
				if (t == TaskService.Errors.CurrentLocationTask) {
					view.Selection.SelectIter (it);
					view.ScrollToCell (view.Model.GetPath (it), view.Columns[0], false, 0, 0);
					it = filter.ConvertIterToChildIter (sort.ConvertIterToChildIter (it));
					store.SetValue (it, DataColumns.Read, true);
					return;
				}
			} while (view.Model.IterNext (ref it));
		}

		internal void SelectTaskListEntry (TaskListEntry taskListEntry)
		{
			TreeIter iter;
			if (!view.Model.GetIterFirst (out iter))
				return;
			do {
				var t = (TaskListEntry) view.Model.GetValue (iter, DataColumns.Task);
				if (t == taskListEntry) {
					view.Selection.SelectIter (iter);
					view.ScrollToCell (view.Model.GetPath (iter), view.Columns[0], false, 0, 0);
					return;
				}
			} while (view.Model.IterNext (ref iter));
		}
		
		void LoadColumnsVisibility ()
		{
			var columns = PropertyService.Get ("Monodevelop.ErrorListColumns", string.Join (";", Enumerable.Repeat ("TRUE", view.Columns.Length)));
			var tokens = columns.Split (new [] { ';' }, StringSplitOptions.RemoveEmptyEntries);
			if (view.Columns.Length == tokens.Length) {
				for (int i = 0; i < tokens.Length; i++) {
					bool visible;
					if (bool.TryParse (tokens [i], out visible))
						view.Columns [i].Visible = visible;
				}
			}
		}

		void StoreColumnsVisibility ()
		{
			PropertyService.Set ("Monodevelop.ErrorListColumns", string.Join (";", view.Columns.Select (c => c.Visible ? "TRUE" : "FALSE")));
		}

		Gtk.Menu CreateMenu ()
		{
			if (menu != null)
				return menu;
			
			var group = new ActionGroup ("Popup");

			var help = new Gtk.Action ("help", GettextCatalog.GetString ("Show Error Reference"),
				GettextCatalog.GetString ("Show Error Reference"), Gtk.Stock.Help);
			help.Activated += OnShowReference;
			group.Add (help, "F1");

			var copy = new Gtk.Action ("copy", GettextCatalog.GetString ("_Copy"),
				GettextCatalog.GetString ("Copy task"), Gtk.Stock.Copy);
			copy.Activated += OnTaskCopied;
			group.Add (copy, "<Control><Mod2>c");

			var jump = new Gtk.Action ("jump", GettextCatalog.GetString ("_Go to"),
				GettextCatalog.GetString ("Go to task"), Gtk.Stock.JumpTo);
			jump.Activated += OnTaskJumpto;
			group.Add (jump);

			var columns = new Gtk.Action ("columns", GettextCatalog.GetString ("Columns"));
			group.Add (columns, null);

			var columnType = new ToggleAction ("columnType", GettextCatalog.GetString ("Type"),
				GettextCatalog.GetString ("Toggle visibility of Type column"), null);
			columnType.Toggled += OnColumnVisibilityChanged;
			columnsActions[columnType] = VisibleColumns.Type;
			group.Add (columnType);

			var columnValidity = new ToggleAction ("columnValidity", GettextCatalog.GetString ("Validity"),
				GettextCatalog.GetString ("Toggle visibility of Validity column"), null);
			columnValidity.Toggled += OnColumnVisibilityChanged;
			columnsActions[columnValidity] = VisibleColumns.Marked;
			group.Add (columnValidity);

			var columnLine = new ToggleAction ("columnLine", GettextCatalog.GetString ("Line"),
				GettextCatalog.GetString ("Toggle visibility of Line column"), null);
			columnLine.Toggled += OnColumnVisibilityChanged;
			columnsActions[columnLine] = VisibleColumns.Line;
			group.Add (columnLine);

			var columnDescription = new ToggleAction ("columnDescription", GettextCatalog.GetString ("Description"),
				GettextCatalog.GetString ("Toggle visibility of Description column"), null);
			columnDescription.Toggled += OnColumnVisibilityChanged;
			columnsActions[columnDescription] = VisibleColumns.Description;
			group.Add (columnDescription);

			var columnFile = new ToggleAction ("columnFile", GettextCatalog.GetString ("File"),
				GettextCatalog.GetString ("Toggle visibility of File column"), null);
			columnFile.Toggled += OnColumnVisibilityChanged;
			columnsActions[columnFile] = VisibleColumns.File;
			group.Add (columnFile);

			var columnProject = new ToggleAction ("columnProject", GettextCatalog.GetString ("Project"),
				GettextCatalog.GetString ("Toggle visibility of Project column"), null);
			columnProject.Toggled += OnColumnVisibilityChanged;
			columnsActions[columnProject] = VisibleColumns.Project;
			group.Add (columnProject);

			var columnPath = new ToggleAction ("columnPath", GettextCatalog.GetString ("Path"),
				GettextCatalog.GetString ("Toggle visibility of Path column"), null);
			columnPath.Toggled += OnColumnVisibilityChanged;
			columnsActions[columnPath] = VisibleColumns.Path;
			group.Add (columnPath);

			var columnCategory = new ToggleAction ("columnCategory", GettextCatalog.GetString ("Category"),
			                                       GettextCatalog.GetString ("Toggle visibility of Category column"), null);
			columnCategory.Toggled += OnColumnVisibilityChanged;
			columnsActions[columnCategory] = VisibleColumns.Category;
			group.Add (columnCategory);



			var uiManager = new UIManager ();
			uiManager.InsertActionGroup (group, 0);
			
			string uiStr = "<ui><popup name='popup'>"
				+ "<menuitem action='help'/>"
				+ "<menuitem action='copy'/>"
				+ "<menuitem action='jump'/>"
				+ "<separator/>"
				+ "<menu action='columns'>"
				+ "<menuitem action='columnType' />"
				+ "<menuitem action='columnValidity' />"
				+ "<menuitem action='columnLine' />"
				+ "<menuitem action='columnDescription' />"
				+ "<menuitem action='columnFile' />"
				+ "<menuitem action='columnProject' />"
				+ "<menuitem action='columnPath' />"
				+ "<menuitem action='columnCategory' />"
				+ "</menu>"
				+ "</popup></ui>";

			uiManager.AddUiFromString (uiStr);
			menu = (Menu)uiManager.GetWidget ("/popup");
			menu.ShowAll ();

			menu.Shown += delegate {
				columnType.Active = view.Columns[VisibleColumns.Type].Visible;
				columnValidity.Active = view.Columns[VisibleColumns.Marked].Visible;
				columnLine.Active = view.Columns[VisibleColumns.Line].Visible;
				columnDescription.Active = view.Columns[VisibleColumns.Description].Visible;
				columnFile.Active = view.Columns[VisibleColumns.File].Visible;
				columnProject.Active = view.Columns[VisibleColumns.Project].Visible;
				columnPath.Active = view.Columns[VisibleColumns.Path].Visible;
				columnCategory.Active = view.Columns[VisibleColumns.Category].Visible;
				help.Sensitive = copy.Sensitive = jump.Sensitive =
					view.Selection != null &&
					view.Selection.CountSelectedRows () > 0 &&
					(columnType.Active ||
						columnValidity.Active ||
						columnLine.Active ||
						columnDescription.Active ||
						columnFile.Active ||
						columnPath.Active);
				string dummyString;
				help.Sensitive &= GetSelectedErrorReference (out dummyString);
			};
			
			return menu;
		}

		TaskListEntry SelectedTask
		{
			get {
				TreeModel model;
				TreeIter iter;
				if (view.Selection.GetSelected (out model, out iter)) 
					return model.GetValue (iter, DataColumns.Task) as TaskListEntry;
				return null; // no one selected
			}
		}

		[CommandHandler (EditCommands.Copy)]
		protected void OnCopy ()
		{
			OnTaskCopied (null, null);
		}

		void OnTaskCopied (object o, EventArgs args)
		{
			TaskListEntry task = SelectedTask;
			if (task != null) {
				StringBuilder text = new StringBuilder ();
				if (!string.IsNullOrEmpty (task.FileName)) {
					text.Append (task.FileName);
					if (task.Line >= 1) {
						text.Append ("(").Append (task.Column);
						if (task.Column >= 0)
							text.Append (",").Append (task.Column);
						text.Append (")");
					}
					text.Append (": ");
				}
				text.Append (task.Severity);
				if (!string.IsNullOrEmpty (task.Code)) {
					text.Append (" ").Append (task.Code);
				}
				text.Append (": ");
				text.Append (task.Description);
				if (task.WorkspaceObject != null)
					text.Append (" (").Append (task.WorkspaceObject.Name).Append (")");

				if (!string.IsNullOrEmpty (task.Category)) {
					text.Append (" ").Append (task.Category);
				}

				clipboard = Clipboard.Get (Gdk.Atom.Intern ("CLIPBOARD", false));
				clipboard.Text = text.ToString ();
				clipboard = Clipboard.Get (Gdk.Atom.Intern ("PRIMARY", false));
				clipboard.Text = text.ToString ();
			}
		}

		void OnShowReference (object o, EventArgs args)
		{
			string reference = null;
			if (GetSelectedErrorReference (out reference)) {
				Process.Start ("http://google.com/search?q=" + System.Web.HttpUtility.UrlEncode (reference));
				return;
			}
		}

		bool GetSelectedErrorReference (out string reference)
		{
			TaskListEntry task = SelectedTask;
			if (task != null && !String.IsNullOrEmpty (task.HelpKeyword)) {
				reference = task.HelpKeyword;
				return true;
			}
			if (task != null && !String.IsNullOrEmpty (task.Code)) {
				reference = task.Code;
				return true;
			}
			reference = null;
			return false;
		}

		void OnTaskJumpto (object o, EventArgs args)
		{
			TreeIter iter;
			TreeModel model;
			if (view.Selection.GetSelected (out model, out iter)) {
				iter = filter.ConvertIterToChildIter (sort.ConvertIterToChildIter (iter));
				store.SetValue (iter, DataColumns.Read, true);
				TaskListEntry task = store.GetValue (iter, DataColumns.Task) as TaskListEntry;
				if (task != null) {
					TaskService.ShowStatus (task);
					task.JumpToPosition ();
					TaskService.Errors.CurrentLocationTask = task;
					IdeApp.Workbench.ActiveLocationList = TaskService.Errors;
				}
			}
		}

		void OnColumnVisibilityChanged (object o, EventArgs args)
		{
			ToggleAction action = o as ToggleAction;
			if (action != null)
			{
				view.Columns[columnsActions[action]].Visible = action.Active;
				StoreColumnsVisibility ();
			}
		}

		void AddColumns ()
		{
			CellRendererImage iconRender = new CellRendererImage ();
			
			Gtk.CellRendererToggle toggleRender = new Gtk.CellRendererToggle ();
			toggleRender.Toggled += new ToggledHandler (ItemToggled);
			
			TreeViewColumn col;
			col = view.AppendColumn ("!", iconRender, "image", DataColumns.Type);
			
			col = view.AppendColumn ("", toggleRender);
			col.SetCellDataFunc (toggleRender, new Gtk.TreeCellDataFunc (ToggleDataFunc));
			
			col = view.AppendColumn (GettextCatalog.GetString ("Line"), view.TextRenderer);
			col.SetCellDataFunc (view.TextRenderer, new Gtk.TreeCellDataFunc (LineDataFunc));

			var descriptionCellRenderer = new DescriptionCellRendererText ();
			view.RegisterRenderForFontChanges (descriptionCellRenderer);
			var descriptionCol = view.AppendColumn (GettextCatalog.GetString ("Description"), descriptionCellRenderer);
			descriptionCol.SetCellDataFunc (descriptionCellRenderer, new Gtk.TreeCellDataFunc (DescriptionDataFunc));
			descriptionCol.Resizable = true;
			descriptionCellRenderer.WrapMode = Pango.WrapMode.Word;
			descriptionCellRenderer.PreferedMaxWidth = IdeApp.Workbench.RootWindow.Allocation.Width / 3;

			descriptionCol.AddNotification("width", delegate
			{
				descriptionCellRenderer.WrapWidth = descriptionCol.Width;
				store.Foreach((model, path, iter) =>
				{
					model.EmitRowChanged(path, iter);
					return false;
				});
			});
			
			col = view.AppendColumn (GettextCatalog.GetString ("File"), view.TextRenderer);
			col.SetCellDataFunc (view.TextRenderer, new Gtk.TreeCellDataFunc (FileDataFunc));
			col.Resizable = true;
			
			col = view.AppendColumn (GettextCatalog.GetString ("Project"), view.TextRenderer);
			col.SetCellDataFunc (view.TextRenderer, new Gtk.TreeCellDataFunc (ProjectDataFunc));
			col.Resizable = true;
			
			col = view.AppendColumn (GettextCatalog.GetString ("Path"), view.TextRenderer);
			col.SetCellDataFunc (view.TextRenderer, new Gtk.TreeCellDataFunc (PathDataFunc));
			col.Resizable = true;

			col = view.AppendColumn (GettextCatalog.GetString ("Category"), view.TextRenderer);
			col.SetCellDataFunc (view.TextRenderer, new Gtk.TreeCellDataFunc (CategoryDataFunc));
			col.Resizable = true;
		}

		static void ToggleDataFunc (Gtk.TreeViewColumn column, Gtk.CellRenderer cell, Gtk.TreeModel model, Gtk.TreeIter iter)
		{
			Gtk.CellRendererToggle toggleRenderer = (Gtk.CellRendererToggle)cell;
			TaskListEntry task = model.GetValue (iter, DataColumns.Task) as TaskListEntry; 
			if (task == null) {
				toggleRenderer.Visible = false;
				return;
			}
			toggleRenderer.Active = task.Completed;
		}
		
		static void LineDataFunc (Gtk.TreeViewColumn column, Gtk.CellRenderer cell, Gtk.TreeModel model, Gtk.TreeIter iter)
		{
			Gtk.CellRendererText textRenderer = (Gtk.CellRendererText)cell;
			TaskListEntry task = model.GetValue (iter, DataColumns.Task) as TaskListEntry; 
			if (task == null) {
				textRenderer.Text = "";
				return;
			}
			SetText (textRenderer, model, iter, task, task.Line != 0 ? task.Line.ToString () : "");
		}

		static void DescriptionDataFunc (Gtk.TreeViewColumn column, Gtk.CellRenderer cell, Gtk.TreeModel model, Gtk.TreeIter iter)
		{
			var textRenderer = (CellRendererText)cell;
			TaskListEntry task = model.GetValue (iter, DataColumns.Task) as TaskListEntry; 
			var text = model.GetValue (iter, DataColumns.Description) as string;
			if (task == null) {
				if (model.IterParent (out iter, iter)) {
					task = model.GetValue (iter, DataColumns.Task) as TaskListEntry;
					if (task == null) {
						textRenderer.Text = "";
						return;
					}
				} else {
					textRenderer.Text = "";
					return;
				}
			}
			SetText (textRenderer, model, iter, task, text);
		}

		static void FileDataFunc (Gtk.TreeViewColumn column, Gtk.CellRenderer cell, Gtk.TreeModel model, Gtk.TreeIter iter)
		{
			Gtk.CellRendererText textRenderer = (Gtk.CellRendererText)cell;
			TaskListEntry task = model.GetValue (iter, DataColumns.Task) as TaskListEntry; 
			if (task == null) {
				textRenderer.Text = "";
				return;
			}
			
			string tmpPath = "";
			string fileName = "";
			try {
				tmpPath = GetPath (task);
				fileName = Path.GetFileName (tmpPath);
			} catch (Exception) { 
				fileName =  tmpPath;
			}
			
			SetText (textRenderer, model, iter, task, fileName);
		}
		
		static string GetPath (TaskListEntry task)
		{
			if (task.WorkspaceObject != null)
				return FileService.AbsoluteToRelativePath (task.WorkspaceObject.BaseDirectory, task.FileName);
			
			return task.FileName;
		}
		
		static void ProjectDataFunc (Gtk.TreeViewColumn column, Gtk.CellRenderer cell, Gtk.TreeModel model, Gtk.TreeIter iter)
		{
			Gtk.CellRendererText textRenderer = (Gtk.CellRendererText)cell;
			TaskListEntry task = model.GetValue (iter, DataColumns.Task) as TaskListEntry; 
			if (task == null) {
				textRenderer.Text = "";
				return;
			}
			SetText (textRenderer, model, iter, task, GetProject(task));
		}
		
		static string GetProject (TaskListEntry task)
		{
			return (task != null && task.WorkspaceObject is SolutionFolderItem)? task.WorkspaceObject.Name: string.Empty;
		}
		
		static void PathDataFunc (Gtk.TreeViewColumn column, Gtk.CellRenderer cell, Gtk.TreeModel model, Gtk.TreeIter iter)
		{
			Gtk.CellRendererText textRenderer = (Gtk.CellRendererText)cell;
			TaskListEntry task = model.GetValue (iter, DataColumns.Task) as TaskListEntry; 
			if (task == null) {
				textRenderer.Text = "";
				return;
			}
			SetText (textRenderer, model, iter, task, GetPath (task));
		}

		static void CategoryDataFunc (Gtk.TreeViewColumn column, Gtk.CellRenderer cell, Gtk.TreeModel model, Gtk.TreeIter iter)
		{
			Gtk.CellRendererText textRenderer = (Gtk.CellRendererText)cell;
			var task = model.GetValue (iter, DataColumns.Task) as TaskListEntry;
			if (task == null) {
				textRenderer.Text = "";
				return;
			}
			SetText (textRenderer, model, iter, task, task.Category ?? "");
		}
		
		static void SetText (CellRendererText textRenderer, TreeModel model, TreeIter iter, TaskListEntry task, string text)
		{
			textRenderer.Text = text;
			textRenderer.Weight = (int)((bool)model.GetValue (iter, DataColumns.Read) ? Pango.Weight.Normal : Pango.Weight.Bold);
			textRenderer.Strikethrough = task.Completed;
		}
		
		void OnCombineOpen(object sender, EventArgs e)
		{
			Clear();
		}
		
		void OnCombineClosed(object sender, EventArgs e)
		{
			Clear();
		}
		
		void OnRowActivated (object o, RowActivatedArgs args)
		{
			OnTaskJumpto (null, null);
		}
		
		public CompilerResults CompilerResults = null;
		
		void FilterChanged (object sender, EventArgs e)
		{
			
			ShowErrors.Value = errorBtn.Active;
			ShowWarnings.Value = warnBtn.Active;
			ShowMessages.Value = msgBtn.Active;
			
			filter.Refilter ();
		}

		bool FilterTasks (TreeModel model, TreeIter iter)
		{
			bool canShow = false;

			try {
				TaskListEntry task = store.GetValue (iter, DataColumns.Task) as TaskListEntry;
				if (task == null)
					return true;
				if (task.Severity == TaskSeverity.Error && errorBtn.Active) canShow = true;
				else if (task.Severity == TaskSeverity.Warning && warnBtn.Active) canShow = true;
				else if (task.Severity == TaskSeverity.Information && msgBtn.Active) canShow = true;

				if (canShow && !string.IsNullOrWhiteSpace (currentSearchPattern)) {
					canShow = (task.Description != null && task.Description.IndexOf (currentSearchPattern, StringComparison.OrdinalIgnoreCase) != -1) ||
						(task.Code != null && task.Code.IndexOf (currentSearchPattern, StringComparison.OrdinalIgnoreCase) != -1) ||
						(task.FileName != null && task.FileName.FileName.IndexOf (currentSearchPattern, StringComparison.OrdinalIgnoreCase) != -1) ||
						(task.WorkspaceObject != null && task.WorkspaceObject.Name != null && task.WorkspaceObject.Name.IndexOf (currentSearchPattern, StringComparison.OrdinalIgnoreCase) != -1) ||
						(task.Category != null && task.Category.IndexOf (currentSearchPattern, StringComparison.OrdinalIgnoreCase) != -1);
				}
			} catch {
				//Not yet fully added
				return false;
			}
			
			return canShow;
		}

		public void ShowResults (object sender, EventArgs e)
		{
			Clear();

			AddTasks (TaskService.Errors);
		}

		private void Clear()
		{
			errorCount = warningCount = infoCount = 0;
			if (view.IsRealized)
				view.ScrollToPoint (0, 0);
			store.Clear ();
			tasks.Clear ();
			UpdateErrorsNum ();
			UpdateWarningsNum ();
			UpdateMessagesNum ();
			UpdatePadIcon ();
		}
		
		void TaskChanged (object sender, TaskEventArgs e)
		{
			this.view.QueueDraw ();
		}
	
		void TaskAdded (object sender, TaskEventArgs e)
		{
			AddTasks (e.Tasks);
		}
		
		public void AddTasks (IEnumerable<TaskListEntry> tasks)
		{
			int n = 1;
			foreach (TaskListEntry t in tasks) {
				AddTaskInternal (t);
				if ((n++ % 100) == 0) {
					// Adding many tasks is a bit slow, so refresh the
					// ui at every block of 100.
					DispatchService.RunPendingEvents ();
				}
			}
			filter.Refilter ();
		}
		
		public void AddTask (TaskListEntry t)
		{
			AddTaskInternal (t);
			filter.Refilter ();
		}
		
		void AddTaskInternal (TaskListEntry t)
		{
			if (tasks.Contains (t)) return;
			
			Xwt.Drawing.Image stock;
			
			switch (t.Severity) {
				case TaskSeverity.Error:
					stock = iconError;
					errorCount++;
					UpdateErrorsNum ();
					break; 
				case TaskSeverity.Warning:
					stock = iconWarning;
					warningCount++;
					UpdateWarningsNum ();	
					break;
				default:
					stock = iconInfo;
					infoCount++;
					UpdateMessagesNum ();
					break;
			}
			
			tasks [t] = t;

			var indexOfNewLine = t.Description.IndexOfAny (new [] { '\n', '\r' });
			if (indexOfNewLine != -1) {
				var iter = store.AppendValues (stock, false, t, t.Description.Remove (indexOfNewLine));
				store.AppendValues (iter, iconEmpty, false, null, t.Description);
			} else {
				store.AppendValues (stock, false, t, t.Description);
			}

			UpdatePadIcon ();
		}

		void UpdateErrorsNum () 
		{
			errorBtn.Label = " " + string.Format(GettextCatalog.GetPluralString("{0} Error", "{0} Errors", errorCount), errorCount);
			errorBtn.Image.Show ();
		}

		void UpdateWarningsNum ()
		{
			warnBtn.Label = " " + string.Format(GettextCatalog.GetPluralString("{0} Warning", "{0} Warnings", warningCount), warningCount); 
			warnBtn.Image.Show ();
		}

		void UpdateMessagesNum ()
		{
			msgBtn.Label = " " + string.Format(GettextCatalog.GetPluralString("{0} Message", "{0} Messages", infoCount), infoCount);
			msgBtn.Image.Show ();
		}

		void UpdatePadIcon ()
		{
			if (errorCount > 0)
				Window.Icon = "md-errors-list-has-errors";
			else if (warningCount > 0)
				Window.Icon = "md-errors-list-has-warnings";
			else
				Window.Icon = "md-errors-list";
		}
		
		private void ItemToggled (object o, ToggledArgs args)
		{
			Gtk.TreeIter iter;
			if (store.GetIterFromString (out iter, args.Path)) {
				TaskListEntry task = (TaskListEntry)store.GetValue (iter, DataColumns.Task);
				task.Completed = !task.Completed;
				TaskService.FireTaskToggleEvent (this, new TaskEventArgs (task));
			}
		}

		static int SeverityIterSort(TreeModel model, TreeIter a, TreeIter z)
		{
			TaskListEntry aTask = model.GetValue(a, DataColumns.Task) as TaskListEntry,
			     zTask = model.GetValue(z, DataColumns.Task) as TaskListEntry;
			     
			return (aTask != null && zTask != null) ?
			       aTask.Severity.CompareTo(zTask.Severity) :
			       0;
		}
		
		static int ProjectIterSort (TreeModel model, TreeIter a, TreeIter z)
		{
			TaskListEntry aTask = model.GetValue (a, DataColumns.Task) as TaskListEntry,
			     zTask = model.GetValue (z, DataColumns.Task) as TaskListEntry;
			     
			return (aTask != null && zTask != null) ?
			       GetProject (aTask).CompareTo (GetProject (zTask)) :
			       0;
		}
		
		static int FileIterSort (TreeModel model, TreeIter a, TreeIter z)
		{
			TaskListEntry aTask = model.GetValue (a, DataColumns.Task) as TaskListEntry,
			     zTask = model.GetValue (z, DataColumns.Task) as TaskListEntry;
			     
			return (aTask != null && zTask != null) ?
			       aTask.FileName.CompareTo (zTask.FileName) :
			       0;
		}
		
		void HandleLogBtnToggled (object sender, EventArgs e)
		{
			var visible = logBtn.Active;
			OutputViewVisible.Value = visible;
			outputView.Visible = visible;
			
			if (initialLogShow && visible && control.IsRealized) {
				initialLogShow = false;
				SetInitialOutputViewSize (control.Allocation.Width);
			}
		}
		
		void SetInitialOutputViewSize (int controlWidth)
		{
			double relPos = LogSeparatorPosition;
			int pos = (int) (controlWidth * relPos);
			pos = Math.Max (30, Math.Min (pos, controlWidth - 30));
			control.Position = pos;
		}

		class DescriptionCellRendererText : CellRendererText
		{
			public int PreferedMaxWidth { get; set; }

			public override void GetSize (Widget widget, ref Gdk.Rectangle cell_area, out int x_offset, out int y_offset, out int width, out int height)
			{
				int originalWrapWidth = WrapWidth;
				WrapWidth = -1;
				// First calculate Width with WrapWidth=-1 which will give us
				// Width of text in one line(without wrapping)
				base.GetSize (widget, ref cell_area, out x_offset, out y_offset, out width, out height);
				int oneLineWidth = width;
				WrapWidth = originalWrapWidth;
				// originalWrapWidth(aka WrapWidth) equals to actual width of Column if oneLineWidth is bigger
				// then column width/height we must recalculate, because Height is atm for one line
				// and not multipline that WrapWidth creates...
				if (oneLineWidth > originalWrapWidth) {
					base.GetSize (widget, ref cell_area, out x_offset, out y_offset, out width, out height);
				}
				width = Math.Min (oneLineWidth, PreferedMaxWidth);
			}
		}
	}
}
