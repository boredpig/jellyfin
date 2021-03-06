﻿using MediaBrowser.Model.Dto;
using MediaBrowser.Controller.Session;

namespace MediaBrowser.Controller.Authentication
{
    public class AuthenticationResult
    {
        /// <summary>
        /// Gets or sets the user.
        /// </summary>
        /// <value>The user.</value>
        public UserDto User { get; set; }

        /// <summary>
        /// Gets or sets the session information.
        /// </summary>
        /// <value>The session information.</value>
        public SessionInfo SessionInfo { get; set; }

        /// <summary>
        /// Gets or sets the authentication token.
        /// </summary>
        /// <value>The authentication token.</value>
        public string AccessToken { get; set; }

        /// <summary>
        /// Gets or sets the server identifier.
        /// </summary>
        /// <value>The server identifier.</value>
        public string ServerId { get; set; }
    }
}
