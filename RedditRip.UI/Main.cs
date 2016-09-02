using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using RedditRip.Core;
using RedditSharp;
using RedditSharp.Things;

namespace RedditRip.UI {

    using System.Runtime.CompilerServices;
    using JetBrains.Annotations;

    public partial class Main : Form {

        private static readonly log4net.ILog Log =
            log4net.LogManager.GetLogger( System.Reflection.MethodBase.GetCurrentMethod().DeclaringType );

        private CancellationTokenSource Cts { get; set; }

        public Main() {
            this.Cts = new CancellationTokenSource();
            InitializeComponent();
        }

        private Task Downloads {
            get; set;
        }

        private List<ImageLink> Links {
            get; set;
        }

        private SearchRange SearchRange {
            get; set;
        }

        private Sorting SortOrder {
            get; set;
        }

        private static IEnumerable<IEnumerable<TResult>> Batch<TResult>( IEnumerable<TResult> source, Int32 size ) {
            TResult[] bucket = null;
            var count = 0;

            foreach ( var item in source ) {
                if ( bucket == null )
                    bucket = new TResult[ size ];

                bucket[ count++ ] = item;
                if ( count != size )
                    continue;

                yield return bucket;

                bucket = null;
                count = 0;
            }

            if ( bucket != null && count > 0 ) {
                yield return bucket.Take( count );
            }
        }

        public void AppendLog( String value ) {
            this.OnUIThread( () => {
                this.txtLog.AppendText( value );
                this.txtLog.SelectionStart = txtLog.Text.Length;
                this.txtLog.ScrollToCaret();
                this.txtLog.Invalidate();
            } );
        }

        protected override void OnFormClosing( FormClosingEventArgs e ) {
            this.Cts?.Cancel();
            base.OnFormClosing( e );
        }

        private void AddLinkNode( TreeNode value ) {
            this.OnUIThread( () => {
                linkTree.Nodes.Add( value );
            } );
        }

        private void bNew_CheckedChanged( Object sender, EventArgs e ) => UpdateSortOrder();

        private void bOnlyNsfw_CheckedChanged( Object sender, EventArgs e ) {
            if ( bOnlyNsfw.Checked ) {
                bAllowNsfw.Checked = true;
            }
            bAllowNsfw.Enabled = !bOnlyNsfw.Checked;
        }

        private void btnAddSub_Click( Object sender, EventArgs e ) {
            if ( !String.IsNullOrWhiteSpace( txtSubReddit.Text ) ) {
                var subs = txtSubReddit.Text.Split( ',' ).Where( x => !String.IsNullOrWhiteSpace( x.Trim() ) ).Select( x => x.Trim() );

                foreach ( var sub in subs ) {
                    if ( !listSubReddits.Items.ContainsKey( sub ) ) {
                        var listViewItem = new ListViewItem {
                            Name = sub,
                            Text = sub
                        };

                        listSubReddits.Items.Add( listViewItem );
                        listSubReddits.SelectedIndices.Clear();
                        listSubReddits.SelectedIndices.Add( listSubReddits.Items.IndexOfKey( sub ) );
                    }
                }
                txtSubReddit.Text = String.Empty;
                this.AcceptButton = btnGetLinks;
            }
        }

        private void btnCancel_Click( Object sender, EventArgs e ) => this.Cts?.Cancel();

        private void btnClearSubs_Click( Object sender, EventArgs e ) => listSubReddits.Items.Clear();

        private void btnDestDir_Click( Object sender, EventArgs e ) {
            SetDestination();
        }

        private void btnDownload_Click( Object sender, EventArgs e ) => StartDownload();

        private void btnGetLinks_Click( Object sender, EventArgs e ) {
            var download = MessageBox.Show( "Download file after getting links?", "Download when done.", MessageBoxButtons.YesNoCancel );

            // ReSharper disable once SwitchStatementMissingSomeCases
            switch ( download ) {
                case DialogResult.Cancel:
                    return;

                case DialogResult.Yes:
                    SetDestination();
                    if ( String.IsNullOrWhiteSpace( txtDestination.Text ) )
                        return;
                    break;
            }

            this.Cts = new CancellationTokenSource();
            btnCancel.Enabled = true;
            btnGetLinks.Enabled = false;
            txtDestination.Enabled = false;
            ClearLinkNodes();

            var subs = this.listSubReddits.Items.Cast<ListViewItem>().Select( item => item.Name ).ToList();

            if ( download == DialogResult.Yes ) {
                new TaskFactory().StartNew(
                    () => GetLinksAsync( subs, txtFilter.Text, bAllowNsfw.Checked, bOnlyNsfw.Checked, SearchRange, SortOrder, this.Cts.Token ),
                    this.Cts.Token ).ContinueWith( x => StartDownload() );
            }
            else {
                new TaskFactory().StartNew(
                    () => GetLinksAsync( subs, txtFilter.Text, bAllowNsfw.Checked, bOnlyNsfw.Checked, SearchRange, SortOrder, this.Cts.Token ),
                    this.Cts.Token );
            }
        }

        private void btnRemoveSub_Click( Object sender, EventArgs e ) {
            foreach ( ListViewItem item in listSubReddits.SelectedItems ) {
                listSubReddits.Items.Remove( item );
            }
        }

        private void bTop_CheckedChanged( Object sender, EventArgs e ) => UpdateSortOrder();

        private void ClearLinkNodes() {
            this.OnUIThread( () => {
                linkTree.Nodes.Clear();
            } );
        }

        private List<ImageLink> CombineLinkLists( IEnumerable<ImageLink> results, List<ImageLink> imageLinks ) {
            foreach ( var link in results ) {
                if ( imageLinks.Exists( x => x.Url == link.Url ) )
                    OutputLine( $"Link already obtained: {link.Url} (XPost {link.Post.Url})", true );
                else
                    imageLinks.Add( link );
            }

            return imageLinks;
        }

        private void DownloadLinks( CancellationToken token ) {
            try {
                var posts = this.Links.GroupBy( x => x.Post.Id ).ToDictionary( x => x.Key, x => x.ToList() );
                var ripper = new Core.RedditRip( txtFilter.Text, false, bAllowNsfw.Checked, bOnlyNsfw.Checked,
                    bVerbose.Checked );
                var tasks = new List<Task>();

                foreach ( var post in posts ) {
                    token.ThrowIfCancellationRequested();
                    var downloadPostTask =
                        ripper.DownloadPost( post, txtDestination.Text, token )
                            .ContinueWith( antecedent => {
                                var link = post.Value.FirstOrDefault();
                                OutputLine( "Finished downloading post: " +
                                           ( link != null ? link.Post.SubredditName + "/" : String.Empty ) + post.Key );
                            }, token );

                    tasks.Add( downloadPostTask );
                }

                var downloadBatches = Batch( tasks, 10 ).ToList();
                var batchCount = downloadBatches.Count;
                Task[] curretTasks = null;
                for ( var i = 0; i < batchCount; i++ ) {
                    var batch = downloadBatches.First().ToList();
                    curretTasks = batch.ToArray();
                    Task.WaitAll( curretTasks );
                    downloadBatches.Remove( batch );
                }

                // ReSharper disable once AssignNullToNotNullAttribute
                if ( curretTasks.ToList().All( x => x.IsCompleted ) ) {
                    OutputLine( "Finished downloading." );
                    EnableGetLinksButton( true );
                }
            }
            catch ( OperationCanceledException ) {
                OutputLine( "Downloads canceled by user." );
            }
            catch ( Exception e ) {
                OutputLine( "Unexpected Error Occured: " + e.Message );
            }
            finally {
                EnableCancelButton( false );
            }
        }

        private void EnableCancelButton( Boolean value ) {
            this.OnUIThread( () => {
                btnCancel.Enabled = value;
            } );
        }

        private void EnableDestinationText( Boolean value ) {
            this.OnUIThread( () => {
                txtDestination.Enabled = value;
            } );
        }

        private void EnableDownloadButton( Boolean value ) {
            this.OnUIThread( () => {
                btnDownload.Enabled = value;
            } );
        }

        private void EnableGetLinksButton( Boolean value ) {
            this.OnUIThread( () => {
                btnGetLinks.Enabled = value;
            } );
        }

        private void exportLinksToolStripMenuItem_Click( Object sender, EventArgs e ) {
            var dialog = new SaveFileDialog {
                Title = "Save Links",
                Filter = "TXT files|*.txt",
            };

            if ( dialog.ShowDialog() == DialogResult.OK ) {
                Directory.CreateDirectory( new FileInfo( dialog.FileName ).Directory.FullName );
                using ( var file = new StreamWriter( dialog.FileName ) ) {
                    var posts = this.Links.GroupBy( x => x.Post.Id ).ToDictionary( x => x.Key, x => x.ToList() );
                    foreach ( var post in posts ) {
                        var count = 1;
                        foreach ( var link in post.Value ) {
                            file.WriteLine( link.Post.Id + "|" + link.Post.SubredditName + "|" +
                                       link.Post.AuthorName + "|" + $"{link.Filename}" + "|" + link.Url );
                            count++;
                        }
                    }
                }
            }
        }

        private async Task<List<ImageLink>> GetImgurLinksFromRedditSub( IEnumerable<String> subReddits, String filter, Boolean allowNsfw, Boolean onlyNsfw, SearchRange searchRange, Sorting sortOrder, CancellationToken token ) {
            var tasks = new List<Task>();

            var ripper = new Core.RedditRip( filter, false, allowNsfw, onlyNsfw, bVerbose.Checked );

            var reddit = new Reddit();
            var imageLinks = new List<ImageLink>();

            if ( subReddits.Any() ) {
                tasks.AddRange(
                    subReddits.Where( x => !String.IsNullOrWhiteSpace( x ) )
                        .Select( sub => ripper.GetImgurLinksFromSubReddit( reddit, sub, searchRange, sortOrder, String.Empty, token ) ) ); //TODO:: Refactor destination out of saving links
            }

            await Task.WhenAll( tasks.ToArray() );

            imageLinks = tasks.Cast<Task<List<ImageLink>>>().Aggregate( imageLinks, ( current, task ) => CombineLinkLists( task.Result, current ) );

            return imageLinks;
        }

        private async void GetLinksAsync( IEnumerable< String > subs, String filter, Boolean allowNsfw, Boolean onlyNsfw, SearchRange searchRange, Sorting sortOrder, CancellationToken token ) {
            try {
                var imgurLinks = await GetImgurLinksFromRedditSub( subs, filter, allowNsfw, onlyNsfw, searchRange, sortOrder, token );
                SetLinks( imgurLinks );
                UpdateLinkTree( token );
            }
            catch ( OperationCanceledException ) {
                this.Links = new List<ImageLink>();
                ClearLinkNodes();
                OutputLine( "Action Canceled by user." );
            }
            catch ( Exception e ) {
                OutputLine( "Unexpected Error Occured: " + e.Message );
            }
            finally {
                EnableCancelButton( false );
            }
        }

        private void importToolStripMenuItem_Click( Object sender, EventArgs e ) {
            var dialog = new OpenFileDialog {
                Title = "Open Text File",
                Filter = "TXT files|*.txt",
            };
            if ( dialog.ShowDialog() == DialogResult.OK ) {
                this.Cts = new CancellationTokenSource();
                using ( var file = new StreamReader( dialog.FileName ) ) {
                    String line;
                    OutputLine( $"Reading file: {dialog.FileName}" );
                    this.Links = new List<ImageLink>();
                    while ( ( line = file.ReadLine() ) != null ) {
                        var link = line.Split( '|' );
                        this.Links.Add( new ImageLink(
                            new Post { Id = link[ 0 ], SubredditName = link[ 1 ], AuthorName = link[ 2 ] }, link[ 4 ].Trim( '.', ',', ':', ':', '|', ' ' ), link[ 3 ] ) );

                        if ( file.EndOfStream )
                            break;
                    }
                }
                OutputLine( $"Finished reading file: {dialog.FileName}" );
                OutputLine( $"{this.Links.Count} links found." );
                var path = Path.GetDirectoryName( this.Links.FirstOrDefault()?.Filename );
                path = path?.Replace( this.Links.FirstOrDefault()?.Post.SubredditName + Path.DirectorySeparatorChar +
                                    this.Links.FirstOrDefault()?.Post.AuthorName, String.Empty ).TrimEnd( Path.DirectorySeparatorChar );

                if ( String.IsNullOrWhiteSpace( txtDestination.Text ) ) {
                    txtDestination.Text = path ?? String.Empty;
                }

                btnCancel.Enabled = true;
                UpdateLinkTree( this.Cts.Token );
            }
        }

        private void listSubReddits_SelectedIndexChanged( Object sender, EventArgs e ) {
            btnRemoveSub.Enabled = listSubReddits.SelectedItems.Count > 0;
            btnGetLinks.Enabled = listSubReddits.Items.Count > 0;
        }

        private void Main_Load( Object sender, EventArgs e ) {
            this.AcceptButton = btnAddSub;
            this.SortOrder = Sorting.Top;
            txtLog.Text = Environment.NewLine;
            trackBar1.SetRange( Enum.GetValues( typeof( SearchRange ) ).Cast<Int32>().Min(), Enum.GetValues( typeof( SearchRange ) ).Cast<Int32>().Max() );
            trackBar1.Value = trackBar1.Maximum;
            UpdateSearchRange();
        }

        [MethodImpl( MethodImplOptions.AggressiveInlining )]
        private void OnUIThread( Action action ) {
            try {
                if ( this.InvokeRequired ) {
                    this.Invoke( action );
                }
                else {
                    action();
                }
            }
            catch {

                // ignored
            }
        }

        private void OutputLine( String message, Boolean verboseMessage = false ) {
            if ( verboseMessage && !bVerbose.Checked ) {
                return;
            }

            Debug.WriteLine( $"{DateTime.Now.ToShortTimeString()}: {message}" );
            Log.Info( message );
        }

        [ CanBeNull ]
        private TreeNode PopulateTreeWithLinks( String subName, CancellationToken token ) {
            var subLinks = this.Links.Where( x => x.Post.SubredditName == subName );

            var imageLinks = subLinks as IList< ImageLink > ?? subLinks.ToList();

            if ( imageLinks.Any() ) {
                var subUsers = imageLinks.Where( x => x.Post.SubredditName == subName ).Select( y => y.Post.AuthorName ).Distinct();
                var subNode = new TreeNode { Name = subName, Text = subName, Tag = "sub" };
                OutputLine( $"Building nodes for {subName}.", true );

                foreach ( var user in subUsers ) {
                    token.ThrowIfCancellationRequested();
                    var posts = imageLinks.Where( x => x.Post.AuthorName == user ).ToList();
                    var userNode = new TreeNode { Name = user, Text = user, Tag = "user" };
                    OutputLine( $"Building nodes for {user}'s posts to {subName}.", true );

                    foreach ( var post in posts.Select( x => x.Post.Title ?? x.Post.Id ).Distinct() ) {
                        token.ThrowIfCancellationRequested();
                        var postLinks = posts.Where( x => x.Post.Title == post || x.Post.Id == post );
                        var postNode = new TreeNode { Name = post, Text = post, Tag = "post" };
                        OutputLine( $"Building nodes for {user}'s post: '{post}' on {subName}", true );

                        foreach ( var link in postLinks ) {
                            token.ThrowIfCancellationRequested();
                            var linkNode = new TreeNode { Name = link.Url, Text = link.Url, Tag = "image" };
                            postNode.Nodes.Add( linkNode );
                        }
                        userNode.Nodes.Add( postNode );
                    }
                    subNode.Nodes.Add( userNode );
                }
                return subNode;
            }

            this.OutputLine( $"No links found for sub: {subName}" );

            return null;
        }

        private void SetDestination() {
            if ( String.IsNullOrWhiteSpace( txtDestination.Text ) || !Directory.Exists( txtDestination.Text ) ) {
                var dialog = new FolderBrowserDialog();
                var result = dialog.ShowDialog();
                if ( result == DialogResult.OK && !String.IsNullOrWhiteSpace( dialog.SelectedPath ) ) {
                    txtDestination.Text = dialog.SelectedPath;
                }
            }
        }

        private void SetLinks( List<ImageLink> value ) {
            this.OnUIThread( () => {
                this.Links = value;
            } );
        }

        private void StartDownload() {
            this.Cts = new CancellationTokenSource();
            SetDestination();

            if ( String.IsNullOrWhiteSpace( txtDestination.Text ) ) {
                return;
            }

            this.EnableDestinationText( false );
            this.EnableGetLinksButton( false );

            this.OutputLine( "Starting downloads...." );
            this.Downloads = new TaskFactory().StartNew( () => this.DownloadLinks( this.Cts.Token ), this.Cts.Token );

            this.EnableCancelButton( true );
            this.EnableDownloadButton( true );
        }

        private void trackBar1_Scroll( Object sender, EventArgs e ) => UpdateSearchRange();

        private void txtSubReddit_TextChanged( Object sender, EventArgs e ) {
            if ( !String.IsNullOrWhiteSpace( txtSubReddit.Text ) )
                this.AcceptButton = btnAddSub;
        }

        private void UpdateLinkTree( CancellationToken token ) {
            try {
                var nodes = new List<TreeNode>();
                var subs = this.Links.Select( x => x.Post.SubredditName ).Distinct().ToList();
                OutputLine( "Building nodes for Link Tree" );
                Parallel.ForEach( subs, sub => nodes.Add( PopulateTreeWithLinks( sub, token ) ) );
                foreach ( var node in nodes.OrderBy( x => x.Text ) ) {
                    AddLinkNode( node );
                }
            }
            catch ( OperationCanceledException ) {
                this.Links = new List<ImageLink>();
                ClearLinkNodes();
                OutputLine( "Action Canceled by user." );
            }
            catch ( Exception e ) {
                OutputLine( "Unexpected Error Occured: " + e.Message );
            }
            finally {
                EnableCancelButton( false );
                EnableDownloadButton( true );
            }
        }

        private void UpdateSearchRange() {
            this.SearchRange = ( SearchRange )trackBar1.Value;
            lbRange.Text = this.SearchRange.ToString();
            var enableFilter = this.SearchRange == Enum.GetValues( typeof( SearchRange ) ).Cast<SearchRange>().Max();
            txtFilter.Enabled = enableFilter;
            bNew.Enabled = !enableFilter;
            bTop.Enabled = !enableFilter;
        }

        private void UpdateSortOrder() {
            if ( bTop.Checked ) {
                this.SortOrder = Sorting.Top;
            }
            if ( bNew.Checked ) {
                this.SortOrder = Sorting.New;
            }

            bNew.Checked = !bTop.Checked;
            bTop.Checked = !bNew.Checked;
            UpdateSearchRange();
        }
    }
}
